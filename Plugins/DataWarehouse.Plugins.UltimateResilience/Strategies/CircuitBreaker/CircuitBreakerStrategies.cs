using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.CircuitBreaker;

/// <summary>
/// Circuit breaker state.
/// </summary>
public enum CircuitBreakerState
{
    /// <summary>Circuit is closed, requests flow normally.</summary>
    Closed,
    /// <summary>Circuit is open, requests fail fast.</summary>
    Open,
    /// <summary>Circuit is testing if the system has recovered.</summary>
    HalfOpen
}

/// <summary>
/// Standard circuit breaker strategy with configurable failure thresholds.
/// </summary>
public sealed class StandardCircuitBreakerStrategy : ResilienceStrategyBase
{
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private DateTimeOffset _lastFailureTime = DateTimeOffset.MinValue;
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();

    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly int _halfOpenSuccessThreshold;

    public StandardCircuitBreakerStrategy()
        : this(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30), halfOpenSuccessThreshold: 2)
    {
    }

    public StandardCircuitBreakerStrategy(int failureThreshold, TimeSpan openDuration, int halfOpenSuccessThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failureThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(halfOpenSuccessThreshold);
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration), "Open duration must be positive.");
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _halfOpenSuccessThreshold = halfOpenSuccessThreshold;
    }

    public override string StrategyId => "circuit-breaker-standard";
    public override string StrategyName => "Standard Circuit Breaker";
    public override string Category => "CircuitBreaker";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Standard Circuit Breaker",
        Description = "Classic circuit breaker pattern that opens after consecutive failures and closes after successful test requests",
        Category = "CircuitBreaker",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the current circuit state.
    /// </summary>
    public CircuitBreakerState State
    {
        get
        {
            lock (_stateLock)
            {
                CheckTransitionFromOpen();
                return _state;
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;

        lock (_stateLock)
        {
            CheckTransitionFromOpen();

            if (_state == CircuitBreakerState.Open)
            {
                RecordCircuitBreakerRejection();
                return new ResilienceResult<T>
                {
                    Success = false,
                    CircuitBreakerOpen = true,
                    Exception = new CircuitBreakerOpenException($"Circuit breaker is open until {_openedAt + _openDuration}"),
                    Attempts = 0,
                    TotalDuration = TimeSpan.Zero
                };
            }
        }

        try
        {
            attempts++;
            var result = await operation(cancellationToken);

            lock (_stateLock)
            {
                OnSuccess();
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = attempts,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                OnFailure();
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = attempts,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                CircuitBreakerOpen = _state == CircuitBreakerState.Open
            };
        }
    }

    private void CheckTransitionFromOpen()
    {
        if (_state == CircuitBreakerState.Open && DateTimeOffset.UtcNow >= _openedAt + _openDuration)
        {
            _state = CircuitBreakerState.HalfOpen;
            _failureCount = 0;
        }
    }

    private void OnSuccess()
    {
        if (_state == CircuitBreakerState.HalfOpen)
        {
            _failureCount++;
            if (_failureCount >= _halfOpenSuccessThreshold)
            {
                _state = CircuitBreakerState.Closed;
                _failureCount = 0;
            }
        }
        else if (_state == CircuitBreakerState.Closed)
        {
            _failureCount = 0;
        }
    }

    private void OnFailure()
    {
        _lastFailureTime = DateTimeOffset.UtcNow;

        if (_state == CircuitBreakerState.HalfOpen)
        {
            _state = CircuitBreakerState.Open;
            _openedAt = DateTimeOffset.UtcNow;
            _failureCount = 0;
        }
        else if (_state == CircuitBreakerState.Closed)
        {
            _failureCount++;
            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _failureCount = 0;
            }
        }
    }

    protected override string? GetCurrentState() => _state.ToString();

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = CircuitBreakerState.Closed;
            _failureCount = 0;
            _lastFailureTime = DateTimeOffset.MinValue;
            _openedAt = DateTimeOffset.MinValue;
        }
    }
}

/// <summary>
/// Sliding window circuit breaker that tracks failure rate over a time window.
/// </summary>
public sealed class SlidingWindowCircuitBreakerStrategy : ResilienceStrategyBase
{
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private readonly ConcurrentQueue<(DateTimeOffset timestamp, bool success)> _window = new();
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();

    private readonly TimeSpan _windowDuration;
    private readonly double _failureRateThreshold;
    private readonly int _minimumRequests;
    private readonly TimeSpan _openDuration;

    public SlidingWindowCircuitBreakerStrategy()
        : this(windowDuration: TimeSpan.FromMinutes(1), failureRateThreshold: 0.5, minimumRequests: 10, openDuration: TimeSpan.FromSeconds(30))
    {
    }

    public SlidingWindowCircuitBreakerStrategy(TimeSpan windowDuration, double failureRateThreshold, int minimumRequests, TimeSpan openDuration)
    {
        if (windowDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(windowDuration), "Window duration must be positive.");
        if (failureRateThreshold <= 0 || failureRateThreshold > 1.0) throw new ArgumentOutOfRangeException(nameof(failureRateThreshold), "Failure rate threshold must be between 0 and 1.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumRequests);
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration), "Open duration must be positive.");
        _windowDuration = windowDuration;
        _failureRateThreshold = failureRateThreshold;
        _minimumRequests = minimumRequests;
        _openDuration = openDuration;
    }

    public override string StrategyId => "circuit-breaker-sliding-window";
    public override string StrategyName => "Sliding Window Circuit Breaker";
    public override string Category => "CircuitBreaker";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Sliding Window Circuit Breaker",
        Description = "Circuit breaker that tracks failure rate over a sliding time window for smoother transitions",
        Category = "CircuitBreaker",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.2,
        MemoryFootprint = "Medium"
    };

    public CircuitBreakerState State
    {
        get
        {
            lock (_stateLock)
            {
                CheckTransitions();
                return _state;
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_stateLock)
        {
            CheckTransitions();
            PruneOldEntries();

            if (_state == CircuitBreakerState.Open)
            {
                RecordCircuitBreakerRejection();
                return new ResilienceResult<T>
                {
                    Success = false,
                    CircuitBreakerOpen = true,
                    Exception = new CircuitBreakerOpenException("Circuit breaker is open"),
                    Attempts = 0,
                    TotalDuration = TimeSpan.Zero
                };
            }
        }

        try
        {
            var result = await operation(cancellationToken);

            lock (_stateLock)
            {
                _window.Enqueue((DateTimeOffset.UtcNow, true));
                CheckFailureRate();
            }

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
            lock (_stateLock)
            {
                _window.Enqueue((DateTimeOffset.UtcNow, false));
                CheckFailureRate();
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                CircuitBreakerOpen = _state == CircuitBreakerState.Open
            };
        }
    }

    private void CheckTransitions()
    {
        if (_state == CircuitBreakerState.Open && DateTimeOffset.UtcNow >= _openedAt + _openDuration)
        {
            _state = CircuitBreakerState.HalfOpen;
        }
    }

    private void PruneOldEntries()
    {
        var cutoff = DateTimeOffset.UtcNow - _windowDuration;
        while (_window.TryPeek(out var entry) && entry.timestamp < cutoff)
        {
            _window.TryDequeue(out _);
        }
    }

    private void CheckFailureRate()
    {
        var entries = _window.ToArray();
        if (entries.Length < _minimumRequests) return;

        var failureRate = (double)entries.Count(e => !e.success) / entries.Length;

        if (_state == CircuitBreakerState.HalfOpen)
        {
            if (failureRate < _failureRateThreshold)
            {
                _state = CircuitBreakerState.Closed;
            }
            else
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (_state == CircuitBreakerState.Closed && failureRate >= _failureRateThreshold)
        {
            _state = CircuitBreakerState.Open;
            _openedAt = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() => _state.ToString();

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = CircuitBreakerState.Closed;
            _openedAt = DateTimeOffset.MinValue;
            while (_window.TryDequeue(out _)) { }
        }
    }
}

/// <summary>
/// Count-based circuit breaker that opens after N consecutive failures.
/// </summary>
public sealed class CountBasedCircuitBreakerStrategy : ResilienceStrategyBase
{
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();

    private readonly int _failureThreshold;
    private readonly int _successThreshold;
    private readonly TimeSpan _openDuration;

    public CountBasedCircuitBreakerStrategy()
        : this(failureThreshold: 5, successThreshold: 3, openDuration: TimeSpan.FromSeconds(30))
    {
    }

    public CountBasedCircuitBreakerStrategy(int failureThreshold, int successThreshold, TimeSpan openDuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failureThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(successThreshold);
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration), "Open duration must be positive.");
        _failureThreshold = failureThreshold;
        _successThreshold = successThreshold;
        _openDuration = openDuration;
    }

    public override string StrategyId => "circuit-breaker-count-based";
    public override string StrategyName => "Count-Based Circuit Breaker";
    public override string Category => "CircuitBreaker";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Count-Based Circuit Breaker",
        Description = "Simple circuit breaker that opens after N consecutive failures and closes after M consecutive successes",
        Category = "CircuitBreaker",
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

        lock (_stateLock)
        {
            if (_state == CircuitBreakerState.Open)
            {
                if (DateTimeOffset.UtcNow >= _openedAt + _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                    _consecutiveSuccesses = 0;
                }
                else
                {
                    RecordCircuitBreakerRejection();
                    return new ResilienceResult<T>
                    {
                        Success = false,
                        CircuitBreakerOpen = true,
                        Exception = new CircuitBreakerOpenException("Circuit breaker is open"),
                        Attempts = 0,
                        TotalDuration = TimeSpan.Zero
                    };
                }
            }
        }

        try
        {
            var result = await operation(cancellationToken);

            lock (_stateLock)
            {
                _consecutiveFailures = 0;
                _consecutiveSuccesses++;

                if (_state == CircuitBreakerState.HalfOpen && _consecutiveSuccesses >= _successThreshold)
                {
                    _state = CircuitBreakerState.Closed;
                }
            }

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
            lock (_stateLock)
            {
                _consecutiveSuccesses = 0;
                _consecutiveFailures++;

                if (_state == CircuitBreakerState.HalfOpen ||
                    (_state == CircuitBreakerState.Closed && _consecutiveFailures >= _failureThreshold))
                {
                    _state = CircuitBreakerState.Open;
                    _openedAt = DateTimeOffset.UtcNow;
                }
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                CircuitBreakerOpen = _state == CircuitBreakerState.Open
            };
        }
    }

    protected override string? GetCurrentState() => _state.ToString();

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = CircuitBreakerState.Closed;
            _consecutiveFailures = 0;
            _consecutiveSuccesses = 0;
            _openedAt = DateTimeOffset.MinValue;
        }
    }
}

/// <summary>
/// Time-based circuit breaker that opens based on failure rate within fixed time buckets.
/// </summary>
public sealed class TimeBasedCircuitBreakerStrategy : ResilienceStrategyBase
{
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private readonly ConcurrentDictionary<long, (int successes, int failures)> _buckets = new();
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();

    private readonly TimeSpan _bucketDuration;
    private readonly int _bucketsToTrack;
    private readonly double _failureRateThreshold;
    private readonly int _minimumRequests;
    private readonly TimeSpan _openDuration;

    public TimeBasedCircuitBreakerStrategy()
        : this(bucketDuration: TimeSpan.FromSeconds(10), bucketsToTrack: 6, failureRateThreshold: 0.5, minimumRequests: 10, openDuration: TimeSpan.FromSeconds(30))
    {
    }

    public TimeBasedCircuitBreakerStrategy(TimeSpan bucketDuration, int bucketsToTrack, double failureRateThreshold, int minimumRequests, TimeSpan openDuration)
    {
        if (bucketDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucketDuration), "Bucket duration must be positive.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketsToTrack);
        if (failureRateThreshold <= 0 || failureRateThreshold > 1.0) throw new ArgumentOutOfRangeException(nameof(failureRateThreshold), "Failure rate threshold must be between 0 and 1.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumRequests);
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration), "Open duration must be positive.");
        _bucketDuration = bucketDuration;
        _bucketsToTrack = bucketsToTrack;
        _failureRateThreshold = failureRateThreshold;
        _minimumRequests = minimumRequests;
        _openDuration = openDuration;
    }

    public override string StrategyId => "circuit-breaker-time-based";
    public override string StrategyName => "Time-Based Circuit Breaker";
    public override string Category => "CircuitBreaker";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Time-Based Circuit Breaker",
        Description = "Circuit breaker using fixed time buckets for accurate failure rate calculation with efficient memory usage",
        Category = "CircuitBreaker",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.15,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_stateLock)
        {
            PruneOldBuckets();

            if (_state == CircuitBreakerState.Open)
            {
                if (DateTimeOffset.UtcNow >= _openedAt + _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                }
                else
                {
                    RecordCircuitBreakerRejection();
                    return new ResilienceResult<T>
                    {
                        Success = false,
                        CircuitBreakerOpen = true,
                        Exception = new CircuitBreakerOpenException("Circuit breaker is open"),
                        Attempts = 0,
                        TotalDuration = TimeSpan.Zero
                    };
                }
            }
        }

        try
        {
            var result = await operation(cancellationToken);

            lock (_stateLock)
            {
                RecordOutcome(true);
                CheckFailureRate();
            }

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
            lock (_stateLock)
            {
                RecordOutcome(false);
                CheckFailureRate();
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                CircuitBreakerOpen = _state == CircuitBreakerState.Open
            };
        }
    }

    private long GetBucketKey()
    {
        return DateTimeOffset.UtcNow.Ticks / _bucketDuration.Ticks;
    }

    private void RecordOutcome(bool success)
    {
        var key = GetBucketKey();
        _buckets.AddOrUpdate(key,
            _ => success ? (1, 0) : (0, 1),
            (_, existing) => success
                ? (existing.successes + 1, existing.failures)
                : (existing.successes, existing.failures + 1));
    }

    private void PruneOldBuckets()
    {
        var currentBucket = GetBucketKey();
        var cutoff = currentBucket - _bucketsToTrack;

        foreach (var key in _buckets.Keys.Where(k => k < cutoff).ToList())
        {
            _buckets.TryRemove(key, out _);
        }
    }

    private void CheckFailureRate()
    {
        var totals = _buckets.Values.Aggregate(
            (successes: 0, failures: 0),
            (acc, bucket) => (acc.successes + bucket.successes, acc.failures + bucket.failures));

        var totalRequests = totals.successes + totals.failures;
        if (totalRequests < _minimumRequests) return;

        var failureRate = (double)totals.failures / totalRequests;

        if (_state == CircuitBreakerState.HalfOpen)
        {
            if (failureRate < _failureRateThreshold)
            {
                _state = CircuitBreakerState.Closed;
                _buckets.Clear();
            }
            else
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (_state == CircuitBreakerState.Closed && failureRate >= _failureRateThreshold)
        {
            _state = CircuitBreakerState.Open;
            _openedAt = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() => _state.ToString();

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = CircuitBreakerState.Closed;
            _openedAt = DateTimeOffset.MinValue;
            _buckets.Clear();
        }
    }
}

/// <summary>
/// Advanced half-open state circuit breaker with gradual traffic increase.
/// </summary>
public sealed class GradualRecoveryCircuitBreakerStrategy : ResilienceStrategyBase
{
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private double _halfOpenPermitRate = 0.0;
    private int _halfOpenSuccesses;
    private int _halfOpenAttempts;
    private readonly object _stateLock = new();
    private readonly Random _random = new();

    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly double _initialPermitRate;
    private readonly double _permitRateIncrement;
    private readonly int _successesPerIncrement;

    public GradualRecoveryCircuitBreakerStrategy()
        : this(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30), initialPermitRate: 0.1, permitRateIncrement: 0.1, successesPerIncrement: 3)
    {
    }

    public GradualRecoveryCircuitBreakerStrategy(int failureThreshold, TimeSpan openDuration, double initialPermitRate, double permitRateIncrement, int successesPerIncrement)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failureThreshold);
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration), "Open duration must be positive.");
        if (initialPermitRate <= 0 || initialPermitRate > 1.0) throw new ArgumentOutOfRangeException(nameof(initialPermitRate), "Initial permit rate must be between 0 and 1.");
        if (permitRateIncrement <= 0 || permitRateIncrement > 1.0) throw new ArgumentOutOfRangeException(nameof(permitRateIncrement), "Permit rate increment must be between 0 and 1.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(successesPerIncrement);
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _initialPermitRate = initialPermitRate;
        _permitRateIncrement = permitRateIncrement;
        _successesPerIncrement = successesPerIncrement;
    }

    public override string StrategyId => "circuit-breaker-gradual-recovery";
    public override string StrategyName => "Gradual Recovery Circuit Breaker";
    public override string Category => "CircuitBreaker";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Gradual Recovery Circuit Breaker",
        Description = "Circuit breaker that gradually increases traffic during recovery to prevent thundering herd",
        Category = "CircuitBreaker",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_stateLock)
        {
            if (_state == CircuitBreakerState.Open)
            {
                if (DateTimeOffset.UtcNow >= _openedAt + _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                    _halfOpenPermitRate = _initialPermitRate;
                    _halfOpenSuccesses = 0;
                    _halfOpenAttempts = 0;
                }
                else
                {
                    RecordCircuitBreakerRejection();
                    return new ResilienceResult<T>
                    {
                        Success = false,
                        CircuitBreakerOpen = true,
                        Exception = new CircuitBreakerOpenException("Circuit breaker is open"),
                        Attempts = 0,
                        TotalDuration = TimeSpan.Zero
                    };
                }
            }

            if (_state == CircuitBreakerState.HalfOpen)
            {
                _halfOpenAttempts++;
                if (_random.NextDouble() > _halfOpenPermitRate)
                {
                    RecordCircuitBreakerRejection();
                    return new ResilienceResult<T>
                    {
                        Success = false,
                        CircuitBreakerOpen = true,
                        Exception = new CircuitBreakerOpenException($"Circuit breaker is half-open (permit rate: {_halfOpenPermitRate:P0})"),
                        Attempts = 0,
                        TotalDuration = TimeSpan.Zero,
                        Metadata = { ["permitRate"] = _halfOpenPermitRate }
                    };
                }
            }
        }

        try
        {
            var result = await operation(cancellationToken);

            lock (_stateLock)
            {
                OnSuccess();
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["permitRate"] = _halfOpenPermitRate }
            };
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                OnFailure();
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                CircuitBreakerOpen = _state == CircuitBreakerState.Open
            };
        }
    }

    private void OnSuccess()
    {
        if (_state == CircuitBreakerState.HalfOpen)
        {
            _halfOpenSuccesses++;
            if (_halfOpenSuccesses >= _successesPerIncrement)
            {
                _halfOpenPermitRate = Math.Min(1.0, _halfOpenPermitRate + _permitRateIncrement);
                _halfOpenSuccesses = 0;

                if (_halfOpenPermitRate >= 1.0)
                {
                    _state = CircuitBreakerState.Closed;
                    _failureCount = 0;
                }
            }
        }
        else if (_state == CircuitBreakerState.Closed)
        {
            _failureCount = 0;
        }
    }

    private void OnFailure()
    {
        if (_state == CircuitBreakerState.HalfOpen)
        {
            _state = CircuitBreakerState.Open;
            _openedAt = DateTimeOffset.UtcNow;
        }
        else if (_state == CircuitBreakerState.Closed)
        {
            _failureCount++;
            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _failureCount = 0;
            }
        }
    }

    protected override string? GetCurrentState() =>
        _state == CircuitBreakerState.HalfOpen ? $"HalfOpen ({_halfOpenPermitRate:P0})" : _state.ToString();

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = CircuitBreakerState.Closed;
            _failureCount = 0;
            _openedAt = DateTimeOffset.MinValue;
            _halfOpenPermitRate = 0.0;
            _halfOpenSuccesses = 0;
            _halfOpenAttempts = 0;
        }
    }
}

/// <summary>
/// Adaptive circuit breaker that dynamically adjusts thresholds based on system health.
/// </summary>
public sealed class AdaptiveCircuitBreakerStrategy : ResilienceStrategyBase
{
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private readonly ConcurrentQueue<(DateTimeOffset timestamp, bool success, TimeSpan latency)> _history = new();
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private double _currentFailureThreshold;
    private TimeSpan _currentOpenDuration;
    private readonly object _stateLock = new();

    private readonly double _baseFailureThreshold;
    private readonly double _minFailureThreshold;
    private readonly double _maxFailureThreshold;
    private readonly TimeSpan _baseOpenDuration;
    private readonly TimeSpan _minOpenDuration;
    private readonly TimeSpan _maxOpenDuration;
    private readonly TimeSpan _historyWindow;
    private readonly int _minimumRequests;
    private readonly TimeSpan _latencyThreshold;

    public AdaptiveCircuitBreakerStrategy()
        : this(
            baseFailureThreshold: 0.5,
            minFailureThreshold: 0.2,
            maxFailureThreshold: 0.8,
            baseOpenDuration: TimeSpan.FromSeconds(30),
            minOpenDuration: TimeSpan.FromSeconds(10),
            maxOpenDuration: TimeSpan.FromMinutes(2),
            historyWindow: TimeSpan.FromMinutes(5),
            minimumRequests: 20,
            latencyThreshold: TimeSpan.FromSeconds(5))
    {
    }

    public AdaptiveCircuitBreakerStrategy(
        double baseFailureThreshold, double minFailureThreshold, double maxFailureThreshold,
        TimeSpan baseOpenDuration, TimeSpan minOpenDuration, TimeSpan maxOpenDuration,
        TimeSpan historyWindow, int minimumRequests, TimeSpan latencyThreshold)
    {
        _baseFailureThreshold = baseFailureThreshold;
        _minFailureThreshold = minFailureThreshold;
        _maxFailureThreshold = maxFailureThreshold;
        _currentFailureThreshold = baseFailureThreshold;

        _baseOpenDuration = baseOpenDuration;
        _minOpenDuration = minOpenDuration;
        _maxOpenDuration = maxOpenDuration;
        _currentOpenDuration = baseOpenDuration;

        _historyWindow = historyWindow;
        _minimumRequests = minimumRequests;
        _latencyThreshold = latencyThreshold;
    }

    public override string StrategyId => "circuit-breaker-adaptive";
    public override string StrategyName => "Adaptive Circuit Breaker";
    public override string Category => "CircuitBreaker";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Adaptive Circuit Breaker",
        Description = "Self-adjusting circuit breaker that adapts thresholds based on observed system behavior and latency patterns",
        Category = "CircuitBreaker",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.3,
        MemoryFootprint = "Medium"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_stateLock)
        {
            PruneHistory();
            AdaptThresholds();

            if (_state == CircuitBreakerState.Open)
            {
                if (DateTimeOffset.UtcNow >= _openedAt + _currentOpenDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                }
                else
                {
                    RecordCircuitBreakerRejection();
                    return new ResilienceResult<T>
                    {
                        Success = false,
                        CircuitBreakerOpen = true,
                        Exception = new CircuitBreakerOpenException("Circuit breaker is open"),
                        Attempts = 0,
                        TotalDuration = TimeSpan.Zero,
                        Metadata =
                        {
                            ["currentThreshold"] = _currentFailureThreshold,
                            ["currentOpenDuration"] = _currentOpenDuration.TotalSeconds
                        }
                    };
                }
            }
        }

        try
        {
            var opStart = DateTimeOffset.UtcNow;
            var result = await operation(cancellationToken);
            var latency = DateTimeOffset.UtcNow - opStart;

            lock (_stateLock)
            {
                _history.Enqueue((DateTimeOffset.UtcNow, true, latency));
                CheckFailureRate();
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["latencyMs"] = latency.TotalMilliseconds,
                    ["currentThreshold"] = _currentFailureThreshold
                }
            };
        }
        catch (Exception ex)
        {
            var latency = DateTimeOffset.UtcNow - startTime;

            lock (_stateLock)
            {
                _history.Enqueue((DateTimeOffset.UtcNow, false, latency));
                CheckFailureRate();
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = latency,
                CircuitBreakerOpen = _state == CircuitBreakerState.Open
            };
        }
    }

    private void PruneHistory()
    {
        var cutoff = DateTimeOffset.UtcNow - _historyWindow;
        while (_history.TryPeek(out var entry) && entry.timestamp < cutoff)
        {
            _history.TryDequeue(out _);
        }
    }

    private void AdaptThresholds()
    {
        var entries = _history.ToArray();
        if (entries.Length < _minimumRequests) return;

        // Calculate average latency
        var avgLatency = TimeSpan.FromMilliseconds(entries.Average(e => e.latency.TotalMilliseconds));
        var failureRate = (double)entries.Count(e => !e.success) / entries.Length;

        // If latency is high, be more aggressive (lower threshold, longer open duration)
        var latencyFactor = avgLatency.TotalMilliseconds / _latencyThreshold.TotalMilliseconds;
        latencyFactor = Math.Clamp(latencyFactor, 0.5, 2.0);

        // Adjust failure threshold inversely to latency (high latency = lower threshold)
        _currentFailureThreshold = Math.Clamp(
            _baseFailureThreshold / latencyFactor,
            _minFailureThreshold,
            _maxFailureThreshold);

        // Adjust open duration proportionally to failure rate
        var durationMultiplier = 1.0 + failureRate;
        _currentOpenDuration = TimeSpan.FromMilliseconds(Math.Clamp(
            _baseOpenDuration.TotalMilliseconds * durationMultiplier,
            _minOpenDuration.TotalMilliseconds,
            _maxOpenDuration.TotalMilliseconds));
    }

    private void CheckFailureRate()
    {
        var entries = _history.ToArray();
        if (entries.Length < _minimumRequests) return;

        var failureRate = (double)entries.Count(e => !e.success) / entries.Length;

        if (_state == CircuitBreakerState.HalfOpen)
        {
            if (failureRate < _currentFailureThreshold)
            {
                _state = CircuitBreakerState.Closed;
            }
            else
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (_state == CircuitBreakerState.Closed && failureRate >= _currentFailureThreshold)
        {
            _state = CircuitBreakerState.Open;
            _openedAt = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() =>
        $"{_state} (threshold: {_currentFailureThreshold:P0}, duration: {_currentOpenDuration.TotalSeconds:F0}s)";

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = CircuitBreakerState.Closed;
            _openedAt = DateTimeOffset.MinValue;
            _currentFailureThreshold = _baseFailureThreshold;
            _currentOpenDuration = _baseOpenDuration;
            while (_history.TryDequeue(out _)) { }
        }
    }
}

/// <summary>
/// Exception thrown when circuit breaker is open.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
    public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException) { }
}
