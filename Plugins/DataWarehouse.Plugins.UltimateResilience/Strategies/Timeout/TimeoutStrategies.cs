using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.Timeout;

/// <summary>
/// Simple timeout strategy.
/// </summary>
public sealed class SimpleTimeoutStrategy : ResilienceStrategyBase
{
    private readonly TimeSpan _timeout;

    public SimpleTimeoutStrategy()
        : this(timeout: TimeSpan.FromSeconds(30))
    {
    }

    public SimpleTimeoutStrategy(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        _timeout = timeout;
    }

    public override string StrategyId => "timeout-simple";
    public override string StrategyName => "Simple Timeout";
    public override string Category => "Timeout";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Simple Timeout",
        Description = "Cancels operations that exceed a fixed timeout duration - essential protection against hung operations",
        Category = "Timeout",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            var result = await operation(timeoutCts.Token);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTimeout();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException($"Operation timed out after {_timeout.TotalSeconds}s"),
                Attempts = 1,
                TotalDuration = _timeout,
                Metadata = { ["timeout"] = _timeout.TotalMilliseconds }
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
}

/// <summary>
/// Cascading timeout strategy for operation hierarchies.
/// </summary>
public sealed class CascadingTimeoutStrategy : ResilienceStrategyBase
{
    private readonly TimeSpan _outerTimeout;
    private readonly TimeSpan _innerTimeout;
    private readonly TimeSpan _perStepTimeout;

    public CascadingTimeoutStrategy()
        : this(outerTimeout: TimeSpan.FromSeconds(60), innerTimeout: TimeSpan.FromSeconds(10), perStepTimeout: TimeSpan.FromSeconds(5))
    {
    }

    public CascadingTimeoutStrategy(TimeSpan outerTimeout, TimeSpan innerTimeout, TimeSpan perStepTimeout)
    {
        if (outerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(outerTimeout), "Outer timeout must be positive.");
        if (innerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(innerTimeout), "Inner timeout must be positive.");
        if (perStepTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perStepTimeout), "Per-step timeout must be positive.");
        if (innerTimeout > outerTimeout) throw new ArgumentOutOfRangeException(nameof(innerTimeout), "Inner timeout must be <= outer timeout.");
        _outerTimeout = outerTimeout;
        _innerTimeout = innerTimeout;
        _perStepTimeout = perStepTimeout;
    }

    public override string StrategyId => "timeout-cascading";
    public override string StrategyName => "Cascading Timeout";
    public override string Category => "Timeout";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Cascading Timeout",
        Description = "Hierarchical timeouts with outer, inner, and per-step limits for complex operation chains",
        Category = "Timeout",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the remaining time based on context.
    /// </summary>
    public TimeSpan GetRemainingTime(ResilienceContext? context)
    {
        if (context?.Data.TryGetValue("OperationStartTime", out var startObj) == true &&
            startObj is DateTimeOffset startTime)
        {
            var elapsed = DateTimeOffset.UtcNow - startTime;
            var remaining = _outerTimeout - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return _outerTimeout;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        context?.Data.TryAdd("OperationStartTime", startTime);

        // Determine effective timeout
        var remainingOuter = GetRemainingTime(context);
        var effectiveTimeout = new[] { remainingOuter, _innerTimeout, _perStepTimeout }.Min();

        if (effectiveTimeout <= TimeSpan.Zero)
        {
            RecordTimeout();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException("Cascading timeout: no time remaining"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var result = await operation(timeoutCts.Token);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["effectiveTimeout"] = effectiveTimeout.TotalMilliseconds,
                    ["remainingOuter"] = (remainingOuter - (DateTimeOffset.UtcNow - startTime)).TotalMilliseconds
                }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTimeout();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException($"Cascading timeout after {effectiveTimeout.TotalSeconds}s"),
                Attempts = 1,
                TotalDuration = effectiveTimeout,
                Metadata = { ["effectiveTimeout"] = effectiveTimeout.TotalMilliseconds }
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
}

/// <summary>
/// Adaptive timeout strategy that adjusts based on observed latency.
/// </summary>
public sealed class AdaptiveTimeoutStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentQueue<TimeSpan> _latencyHistory = new();
    private TimeSpan _currentTimeout;
    private readonly object _adaptLock = new();

    private readonly TimeSpan _baseTimeout;
    private readonly TimeSpan _minTimeout;
    private readonly TimeSpan _maxTimeout;
    private readonly double _percentile;
    private readonly double _multiplier;

    public AdaptiveTimeoutStrategy()
        : this(
            baseTimeout: TimeSpan.FromSeconds(10),
            minTimeout: TimeSpan.FromSeconds(1),
            maxTimeout: TimeSpan.FromSeconds(60),
            percentile: 0.99,
            multiplier: 1.5)
    {
    }

    public AdaptiveTimeoutStrategy(
        TimeSpan baseTimeout, TimeSpan minTimeout, TimeSpan maxTimeout,
        double percentile, double multiplier)
    {
        if (baseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseTimeout), "Base timeout must be positive.");
        if (minTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minTimeout), "Min timeout must be positive.");
        if (maxTimeout < minTimeout) throw new ArgumentOutOfRangeException(nameof(maxTimeout), "Max timeout must be >= min timeout.");
        if (percentile <= 0 || percentile > 1.0) throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0 and 1.");
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1.0);
        _baseTimeout = baseTimeout;
        _minTimeout = minTimeout;
        _maxTimeout = maxTimeout;
        _percentile = percentile;
        _multiplier = multiplier;
        _currentTimeout = baseTimeout;
    }

    public override string StrategyId => "timeout-adaptive";
    public override string StrategyName => "Adaptive Timeout";
    public override string Category => "Timeout";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Adaptive Timeout",
        Description = "Self-adjusting timeout based on observed P99 latency - balances responsiveness and success rate",
        Category = "Timeout",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current adaptive timeout.
    /// </summary>
    public TimeSpan CurrentTimeout
    {
        get
        {
            lock (_adaptLock)
            {
                return _currentTimeout;
            }
        }
    }

    private void AdaptTimeout()
    {
        var samples = _latencyHistory.ToArray();
        if (samples.Length < 10) return;

        // Calculate percentile latency
        var sorted = samples.OrderBy(t => t.TotalMilliseconds).ToArray();
        var p99Index = (int)(sorted.Length * _percentile);
        var p99Latency = sorted[Math.Min(p99Index, sorted.Length - 1)];

        // Set timeout to percentile * multiplier
        var newTimeout = TimeSpan.FromMilliseconds(p99Latency.TotalMilliseconds * _multiplier);
        _currentTimeout = TimeSpan.FromMilliseconds(
            Math.Clamp(newTimeout.TotalMilliseconds, _minTimeout.TotalMilliseconds, _maxTimeout.TotalMilliseconds));
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        TimeSpan effectiveTimeout;

        lock (_adaptLock)
        {
            AdaptTimeout();
            effectiveTimeout = _currentTimeout;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var result = await operation(timeoutCts.Token);
            var latency = DateTimeOffset.UtcNow - startTime;

            // Record successful latency
            _latencyHistory.Enqueue(latency);
            while (_latencyHistory.Count > 100)
            {
                _latencyHistory.TryDequeue(out _);
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = latency,
                Metadata =
                {
                    ["adaptiveTimeout"] = effectiveTimeout.TotalMilliseconds,
                    ["actualLatency"] = latency.TotalMilliseconds
                }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTimeout();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException($"Adaptive timeout after {effectiveTimeout.TotalSeconds}s"),
                Attempts = 1,
                TotalDuration = effectiveTimeout,
                Metadata = { ["adaptiveTimeout"] = effectiveTimeout.TotalMilliseconds }
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
        lock (_adaptLock)
        {
            _currentTimeout = _baseTimeout;
            while (_latencyHistory.TryDequeue(out _)) { }
        }
    }

    protected override string? GetCurrentState() =>
        $"Timeout: {_currentTimeout.TotalSeconds:F1}s (range: {_minTimeout.TotalSeconds}s-{_maxTimeout.TotalSeconds}s)";
}

/// <summary>
/// Pessimistic timeout that cancels immediately when timeout is reached.
/// </summary>
public sealed class PessimisticTimeoutStrategy : ResilienceStrategyBase
{
    private readonly TimeSpan _timeout;

    public PessimisticTimeoutStrategy()
        : this(timeout: TimeSpan.FromSeconds(30))
    {
    }

    public PessimisticTimeoutStrategy(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        _timeout = timeout;
    }

    public override string StrategyId => "timeout-pessimistic";
    public override string StrategyName => "Pessimistic Timeout";
    public override string Category => "Timeout";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Pessimistic Timeout",
        Description = "Aggressively cancels operation on timeout - operation may continue in background but result is discarded",
        Category = "Timeout",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
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

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var operationTask = operation(linkedCts.Token);
        var timeoutTask = Task.Delay(_timeout, cancellationToken);

        var completedTask = await Task.WhenAny(operationTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            // Cancel the operation
            timeoutCts.Cancel();
            RecordTimeout();

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException($"Pessimistic timeout after {_timeout.TotalSeconds}s"),
                Attempts = 1,
                TotalDuration = _timeout,
                Metadata =
                {
                    ["timeout"] = _timeout.TotalMilliseconds,
                    ["operationStillRunning"] = !operationTask.IsCompleted
                }
            };
        }

        try
        {
            var result = await operationTask;

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
}

/// <summary>
/// Optimistic timeout that waits for operation to complete naturally if possible.
/// </summary>
public sealed class OptimisticTimeoutStrategy : ResilienceStrategyBase
{
    private readonly TimeSpan _hardTimeout;
    private readonly TimeSpan _softTimeout;

    public OptimisticTimeoutStrategy()
        : this(hardTimeout: TimeSpan.FromSeconds(60), softTimeout: TimeSpan.FromSeconds(30))
    {
    }

    public OptimisticTimeoutStrategy(TimeSpan hardTimeout, TimeSpan softTimeout)
    {
        if (hardTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(hardTimeout), "Hard timeout must be positive.");
        if (softTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(softTimeout), "Soft timeout must be positive.");
        if (softTimeout > hardTimeout) throw new ArgumentOutOfRangeException(nameof(softTimeout), "Soft timeout must be <= hard timeout.");
        _hardTimeout = hardTimeout;
        _softTimeout = softTimeout;
    }

    public override string StrategyId => "timeout-optimistic";
    public override string StrategyName => "Optimistic Timeout";
    public override string Category => "Timeout";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Optimistic Timeout",
        Description = "Uses soft timeout for initial signal and hard timeout as absolute limit - balances responsiveness with completion",
        Category = "Timeout",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
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

        // Soft timeout: we signal but don't force cancel
        using var softCts = new CancellationTokenSource();
        softCts.CancelAfter(_softTimeout);

        // Hard timeout: absolute cancellation
        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardCts.CancelAfter(_hardTimeout);

        // Pass soft cancellation to operation (cooperative)
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(softCts.Token, cancellationToken);

        try
        {
            var result = await operation(linkedCts.Token).WaitAsync(hardCts.Token);

            var duration = DateTimeOffset.UtcNow - startTime;
            var exceededSoft = duration > _softTimeout;

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = duration,
                Metadata =
                {
                    ["exceededSoftTimeout"] = exceededSoft,
                    ["softTimeout"] = _softTimeout.TotalMilliseconds,
                    ["hardTimeout"] = _hardTimeout.TotalMilliseconds
                }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTimeout();
            var duration = DateTimeOffset.UtcNow - startTime;
            var isHardTimeout = duration >= _hardTimeout * 0.95;

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException(
                    isHardTimeout
                        ? $"Hard timeout after {_hardTimeout.TotalSeconds}s"
                        : $"Soft timeout after {_softTimeout.TotalSeconds}s"),
                Attempts = 1,
                TotalDuration = duration,
                Metadata = { ["isHardTimeout"] = isHardTimeout }
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
}

/// <summary>
/// Per-attempt timeout for use with retry strategies.
/// </summary>
public sealed class PerAttemptTimeoutStrategy : ResilienceStrategyBase
{
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _totalTimeout;

    public PerAttemptTimeoutStrategy()
        : this(attemptTimeout: TimeSpan.FromSeconds(10), totalTimeout: TimeSpan.FromSeconds(60))
    {
    }

    public PerAttemptTimeoutStrategy(TimeSpan attemptTimeout, TimeSpan totalTimeout)
    {
        if (attemptTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(attemptTimeout), "Attempt timeout must be positive.");
        if (totalTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(totalTimeout), "Total timeout must be positive.");
        if (attemptTimeout > totalTimeout) throw new ArgumentOutOfRangeException(nameof(attemptTimeout), "Attempt timeout must be <= total timeout.");
        _attemptTimeout = attemptTimeout;
        _totalTimeout = totalTimeout;
    }

    public override string StrategyId => "timeout-per-attempt";
    public override string StrategyName => "Per-Attempt Timeout";
    public override string Category => "Timeout";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Per-Attempt Timeout",
        Description = "Applies timeout per attempt within an overall total timeout - designed for use with retry strategies",
        Category = "Timeout",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the remaining time for the total operation.
    /// </summary>
    public TimeSpan GetRemainingTotal(DateTimeOffset operationStart)
    {
        var elapsed = DateTimeOffset.UtcNow - operationStart;
        var remaining = _totalTimeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        // Get operation start time from context or use current
        var operationStart = context?.Data.TryGetValue("OperationStartTime", out var startObj) == true &&
            startObj is DateTimeOffset start ? start : startTime;

        var remainingTotal = GetRemainingTotal(operationStart);

        if (remainingTotal <= TimeSpan.Zero)
        {
            RecordTimeout();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException("Total timeout exceeded"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        // Use the smaller of attempt timeout or remaining total
        var effectiveTimeout = remainingTotal < _attemptTimeout ? remainingTotal : _attemptTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var result = await operation(timeoutCts.Token);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["attemptTimeout"] = _attemptTimeout.TotalMilliseconds,
                    ["effectiveTimeout"] = effectiveTimeout.TotalMilliseconds,
                    ["remainingTotal"] = (remainingTotal - (DateTimeOffset.UtcNow - startTime)).TotalMilliseconds
                }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTimeout();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new TimeoutRejectedException($"Attempt timed out after {effectiveTimeout.TotalSeconds}s"),
                Attempts = 1,
                TotalDuration = effectiveTimeout,
                Metadata = { ["effectiveTimeout"] = effectiveTimeout.TotalMilliseconds }
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
}

/// <summary>
/// Exception thrown when a timeout is reached.
/// </summary>
public sealed class TimeoutRejectedException : Exception
{
    public TimeoutRejectedException(string message) : base(message) { }
    public TimeoutRejectedException(string message, Exception innerException) : base(message, innerException) { }
}
