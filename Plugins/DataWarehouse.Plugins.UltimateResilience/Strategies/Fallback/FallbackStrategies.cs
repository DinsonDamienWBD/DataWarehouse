using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.Fallback;

/// <summary>
/// Cache-based fallback strategy.
/// </summary>
public sealed class CacheFallbackStrategy<TResult> : ResilienceStrategyBase
{
    private readonly ConcurrentDictionary<string, (TResult value, DateTimeOffset timestamp)> _cache = new();
    private readonly TimeSpan _cacheTtl;
    private readonly Func<ResilienceContext?, string> _keySelector;

    public CacheFallbackStrategy()
        : this(cacheTtl: TimeSpan.FromMinutes(5), keySelector: ctx => ctx?.OperationName ?? "default")
    {
    }

    public CacheFallbackStrategy(TimeSpan cacheTtl, Func<ResilienceContext?, string> keySelector)
    {
        if (cacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheTtl), "Cache TTL must be positive.");
        _cacheTtl = cacheTtl;
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
    }

    public override string StrategyId => "fallback-cache";
    public override string StrategyName => "Cache Fallback";
    public override string Category => "Fallback";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Cache Fallback",
        Description = "Falls back to cached values when primary operation fails - provides stale data rather than errors",
        Category = "Fallback",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current cache size.
    /// </summary>
    public int CacheSize => _cache.Count;

    /// <summary>
    /// Manually adds a value to the cache.
    /// </summary>
    public void CacheValue(string key, TResult value)
    {
        _cache[key] = (value, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Clears expired entries from the cache.
    /// </summary>
    public void PruneCache()
    {
        var cutoff = DateTimeOffset.UtcNow - _cacheTtl;
        foreach (var key in _cache.Keys)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.timestamp < cutoff)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var cacheKey = _keySelector(context);

        try
        {
            var result = await operation(cancellationToken);

            // Cache the successful result
            if (result is TResult typedResult)
            {
                _cache[cacheKey] = (typedResult, DateTimeOffset.UtcNow);
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false
            };
        }
        catch (Exception ex)
        {
            // Try to use cached value
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                var age = DateTimeOffset.UtcNow - cached.timestamp;

                if (cached.value is T fallbackValue)
                {
                    RecordFallback();
                    return new ResilienceResult<T>
                    {
                        Success = true,
                        Value = fallbackValue,
                        Attempts = 1,
                        TotalDuration = DateTimeOffset.UtcNow - startTime,
                        UsedFallback = true,
                        Metadata =
                        {
                            ["cacheAge"] = age.TotalSeconds,
                            ["isStale"] = age > _cacheTtl,
                            ["originalException"] = ex.Message
                        }
                    };
                }
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false,
                Metadata = { ["cacheKey"] = cacheKey, ["cacheHit"] = false }
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        _cache.Clear();
    }

    protected override string? GetCurrentState() => $"Cache entries: {_cache.Count}";
}

/// <summary>
/// Default value fallback strategy.
/// </summary>
public sealed class DefaultValueFallbackStrategy<TResult> : ResilienceStrategyBase
{
    private readonly TResult _defaultValue;
    private readonly Func<Exception, bool>? _shouldFallback;

    public DefaultValueFallbackStrategy(TResult defaultValue)
        : this(defaultValue, shouldFallback: null)
    {
    }

    public DefaultValueFallbackStrategy(TResult defaultValue, Func<Exception, bool>? shouldFallback)
    {
        _defaultValue = defaultValue;
        _shouldFallback = shouldFallback;
    }

    public override string StrategyId => "fallback-default-value";
    public override string StrategyName => "Default Value Fallback";
    public override string Category => "Fallback";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Default Value Fallback",
        Description = "Returns a predefined default value when the primary operation fails - simple and predictable",
        Category = "Fallback",
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

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false
            };
        }
        catch (Exception ex) when (_shouldFallback?.Invoke(ex) != false)
        {
            RecordFallback();

            if (_defaultValue is T typedDefault)
            {
                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = typedDefault,
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    UsedFallback = true,
                    Metadata = { ["originalException"] = ex.Message }
                };
            }

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
/// Degraded service fallback strategy.
/// </summary>
public sealed class DegradedServiceFallbackStrategy<TResult> : ResilienceStrategyBase
{
    private readonly Func<ResilienceContext?, CancellationToken, Task<TResult>> _degradedOperation;
    private readonly Func<Exception, bool>? _shouldDegrade;

    public DegradedServiceFallbackStrategy(Func<ResilienceContext?, CancellationToken, Task<TResult>> degradedOperation)
        : this(degradedOperation, shouldDegrade: null)
    {
    }

    public DegradedServiceFallbackStrategy(
        Func<ResilienceContext?, CancellationToken, Task<TResult>> degradedOperation,
        Func<Exception, bool>? shouldDegrade)
    {
        _degradedOperation = degradedOperation;
        _shouldDegrade = shouldDegrade;
    }

    public override string StrategyId => "fallback-degraded-service";
    public override string StrategyName => "Degraded Service Fallback";
    public override string Category => "Fallback";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Degraded Service Fallback",
        Description = "Falls back to a simplified or degraded version of the operation - maintains functionality with reduced features",
        Category = "Fallback",
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

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false,
                Metadata = { ["mode"] = "full" }
            };
        }
        catch (Exception ex) when (_shouldDegrade?.Invoke(ex) != false)
        {
            RecordFallback();

            try
            {
                var degradedResult = await _degradedOperation(context, cancellationToken);

                if (degradedResult is T typedResult)
                {
                    return new ResilienceResult<T>
                    {
                        Success = true,
                        Value = typedResult,
                        Attempts = 1,
                        TotalDuration = DateTimeOffset.UtcNow - startTime,
                        UsedFallback = true,
                        Metadata =
                        {
                            ["mode"] = "degraded",
                            ["originalException"] = ex.Message
                        }
                    };
                }
            }
            catch (Exception degradedEx)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new AggregateException("Both primary and degraded operations failed", ex, degradedEx),
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    UsedFallback = true
                };
            }

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
/// Failover fallback strategy for switching between multiple services.
/// </summary>
public sealed class FailoverFallbackStrategy : ResilienceStrategyBase
{
    private readonly List<Func<CancellationToken, Task<object>>> _operations;
    private int _primaryIndex;
    private readonly object _failoverLock = new();

    public FailoverFallbackStrategy(params Func<CancellationToken, Task<object>>[] operations)
    {
        _operations = new List<Func<CancellationToken, Task<object>>>(operations);
    }

    public override string StrategyId => "fallback-failover";
    public override string StrategyName => "Failover Fallback";
    public override string Category => "Fallback";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Failover Fallback",
        Description = "Switches to backup services when primary fails - supports multiple fallback levels",
        Category = "Fallback",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the current primary service index.
    /// </summary>
    public int PrimaryIndex
    {
        get
        {
            lock (_failoverLock)
            {
                return _primaryIndex;
            }
        }
    }

    /// <summary>
    /// Manually sets the primary service.
    /// </summary>
    public void SetPrimary(int index)
    {
        if (index < 0 || index >= _operations.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        lock (_failoverLock)
        {
            _primaryIndex = index;
        }
    }

    /// <summary>
    /// Adds a new service to the failover chain.
    /// </summary>
    public void AddService(Func<CancellationToken, Task<object>> operation)
    {
        lock (_failoverLock)
        {
            _operations.Add(operation);
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var exceptions = new List<Exception>();
        var currentIndex = _primaryIndex;

        // Try all services starting from primary
        for (int attempt = 0; attempt < _operations.Count; attempt++)
        {
            var serviceIndex = (currentIndex + attempt) % _operations.Count;

            try
            {
                var result = await _operations[serviceIndex](cancellationToken);

                // On success, update primary if we failed over
                if (attempt > 0)
                {
                    RecordFallback();
                    lock (_failoverLock)
                    {
                        _primaryIndex = serviceIndex;
                    }
                }

                if (result is T typedResult)
                {
                    return new ResilienceResult<T>
                    {
                        Success = true,
                        Value = typedResult,
                        Attempts = attempt + 1,
                        TotalDuration = DateTimeOffset.UtcNow - startTime,
                        UsedFallback = attempt > 0,
                        Metadata =
                        {
                            ["serviceIndex"] = serviceIndex,
                            ["failoverCount"] = attempt
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = new AggregateException("All failover services failed", exceptions),
            Attempts = _operations.Count,
            TotalDuration = DateTimeOffset.UtcNow - startTime,
            UsedFallback = true
        };
    }

    public override void Reset()
    {
        base.Reset();
        lock (_failoverLock)
        {
            _primaryIndex = 0;
        }
    }

    protected override string? GetCurrentState() => $"Primary: service[{_primaryIndex}] of {_operations.Count}";
}

/// <summary>
/// Circuit breaker combined with fallback strategy.
/// </summary>
public sealed class CircuitBreakerFallbackStrategy<TResult> : ResilienceStrategyBase
{
    private enum State { Closed, Open, HalfOpen }

    private State _state = State.Closed;
    private int _failureCount;
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();

    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TResult _fallbackValue;
    private readonly Func<ResilienceContext?, CancellationToken, Task<TResult>>? _fallbackOperation;

    public CircuitBreakerFallbackStrategy(TResult fallbackValue)
        : this(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30), fallbackValue, fallbackOperation: null)
    {
    }

    public CircuitBreakerFallbackStrategy(
        int failureThreshold, TimeSpan openDuration,
        TResult fallbackValue,
        Func<ResilienceContext?, CancellationToken, Task<TResult>>? fallbackOperation = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _fallbackValue = fallbackValue;
        _fallbackOperation = fallbackOperation;
    }

    public override string StrategyId => "fallback-circuit-breaker";
    public override string StrategyName => "Circuit Breaker with Fallback";
    public override string Category => "Fallback";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Circuit Breaker with Fallback",
        Description = "Combines circuit breaker protection with fallback value - fast fails to fallback when circuit is open",
        Category = "Fallback",
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
        bool isCircuitOpen;

        lock (_stateLock)
        {
            if (_state == State.Open)
            {
                if (DateTimeOffset.UtcNow >= _openedAt + _openDuration)
                {
                    _state = State.HalfOpen;
                    isCircuitOpen = false;
                }
                else
                {
                    // Circuit is open - return fallback immediately
                    RecordFallback();
                    RecordCircuitBreakerRejection();
                    isCircuitOpen = true;
                }
            }
            else
            {
                isCircuitOpen = false;
            }
        }

        if (isCircuitOpen)
        {
            return await GetFallbackResultAsync<T>(startTime, context, cancellationToken, isCircuitOpen: true);
        }

        try
        {
            var result = await operation(cancellationToken);

            lock (_stateLock)
            {
                _failureCount = 0;
                _state = State.Closed;
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false
            };
        }
        catch (Exception)
        {
            lock (_stateLock)
            {
                _failureCount++;
                if (_state == State.HalfOpen || _failureCount >= _failureThreshold)
                {
                    _state = State.Open;
                    _openedAt = DateTimeOffset.UtcNow;
                }
            }

            RecordFallback();
            return await GetFallbackResultAsync<T>(startTime, context, cancellationToken, isCircuitOpen: false);
        }
    }

    private async Task<ResilienceResult<T>> GetFallbackResultAsync<T>(
        DateTimeOffset startTime,
        ResilienceContext? context,
        CancellationToken cancellationToken,
        bool isCircuitOpen)
    {
        if (_fallbackOperation != null)
        {
            try
            {
                var fallbackResult = await _fallbackOperation(context, cancellationToken);
                if (fallbackResult is T typedFallback)
                {
                    return new ResilienceResult<T>
                    {
                        Success = true,
                        Value = typedFallback,
                        Attempts = 1,
                        TotalDuration = DateTimeOffset.UtcNow - startTime,
                        UsedFallback = true,
                        CircuitBreakerOpen = isCircuitOpen,
                        Metadata = { ["fallbackSource"] = "operation" }
                    };
                }
            }
            catch
            {
                // Fall through to static value
            }
        }

        if (_fallbackValue is T typedDefault)
        {
            return new ResilienceResult<T>
            {
                Success = true,
                Value = typedDefault,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = true,
                CircuitBreakerOpen = isCircuitOpen,
                Metadata = { ["fallbackSource"] = "value" }
            };
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = new InvalidOperationException("Fallback value type mismatch"),
            Attempts = 1,
            TotalDuration = DateTimeOffset.UtcNow - startTime,
            CircuitBreakerOpen = isCircuitOpen
        };
    }

    public override void Reset()
    {
        base.Reset();
        lock (_stateLock)
        {
            _state = State.Closed;
            _failureCount = 0;
            _openedAt = DateTimeOffset.MinValue;
        }
    }

    protected override string? GetCurrentState() => _state.ToString();
}

/// <summary>
/// Conditional fallback strategy that applies different fallbacks based on exception type.
/// </summary>
public sealed class ConditionalFallbackStrategy<TResult> : ResilienceStrategyBase
{
    private readonly List<(Func<Exception, bool> predicate, Func<Exception, ResilienceContext?, CancellationToken, Task<TResult>> fallback)> _handlers = new();
    private readonly Func<Exception, ResilienceContext?, CancellationToken, Task<TResult>>? _defaultFallback;

    public ConditionalFallbackStrategy(Func<Exception, ResilienceContext?, CancellationToken, Task<TResult>>? defaultFallback = null)
    {
        _defaultFallback = defaultFallback;
    }

    public override string StrategyId => "fallback-conditional";
    public override string StrategyName => "Conditional Fallback";
    public override string Category => "Fallback";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Conditional Fallback",
        Description = "Applies different fallback strategies based on the type or characteristics of the exception",
        Category = "Fallback",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Adds a conditional fallback handler.
    /// </summary>
    public ConditionalFallbackStrategy<TResult> AddHandler(
        Func<Exception, bool> predicate,
        Func<Exception, ResilienceContext?, CancellationToken, Task<TResult>> fallback)
    {
        _handlers.Add((predicate, fallback));
        return this;
    }

    /// <summary>
    /// Adds a handler for a specific exception type.
    /// </summary>
    public ConditionalFallbackStrategy<TResult> AddHandler<TException>(
        Func<TException, ResilienceContext?, CancellationToken, Task<TResult>> fallback)
        where TException : Exception
    {
        _handlers.Add((
            ex => ex is TException,
            (ex, ctx, ct) => fallback((TException)ex, ctx, ct)));
        return this;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false
            };
        }
        catch (Exception ex)
        {
            RecordFallback();

            // Find matching handler
            foreach (var (predicate, fallback) in _handlers)
            {
                if (predicate(ex))
                {
                    try
                    {
                        var fallbackResult = await fallback(ex, context, cancellationToken);

                        if (fallbackResult is T typedResult)
                        {
                            return new ResilienceResult<T>
                            {
                                Success = true,
                                Value = typedResult,
                                Attempts = 1,
                                TotalDuration = DateTimeOffset.UtcNow - startTime,
                                UsedFallback = true,
                                Metadata =
                                {
                                    ["originalException"] = ex.GetType().Name,
                                    ["handlerMatched"] = true
                                }
                            };
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        return new ResilienceResult<T>
                        {
                            Success = false,
                            Exception = new AggregateException("Fallback handler failed", ex, fallbackEx),
                            Attempts = 1,
                            TotalDuration = DateTimeOffset.UtcNow - startTime,
                            UsedFallback = true
                        };
                    }
                }
            }

            // Try default fallback
            if (_defaultFallback != null)
            {
                try
                {
                    var defaultResult = await _defaultFallback(ex, context, cancellationToken);

                    if (defaultResult is T typedDefault)
                    {
                        return new ResilienceResult<T>
                        {
                            Success = true,
                            Value = typedDefault,
                            Attempts = 1,
                            TotalDuration = DateTimeOffset.UtcNow - startTime,
                            UsedFallback = true,
                            Metadata = { ["originalException"] = ex.GetType().Name, ["usedDefault"] = true }
                        };
                    }
                }
                catch (Exception defaultEx)
                {
                    return new ResilienceResult<T>
                    {
                        Success = false,
                        Exception = new AggregateException("Default fallback failed", ex, defaultEx),
                        Attempts = 1,
                        TotalDuration = DateTimeOffset.UtcNow - startTime,
                        UsedFallback = true
                    };
                }
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                UsedFallback = false
            };
        }
    }
}
