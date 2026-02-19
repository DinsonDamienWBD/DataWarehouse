using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.Bulkhead;

/// <summary>
/// Thread pool isolation bulkhead strategy.
/// </summary>
public sealed class ThreadPoolBulkheadStrategy : ResilienceStrategyBase, IDisposable
{
    private readonly SemaphoreSlim _executionSemaphore;
    private readonly SemaphoreSlim _queueSemaphore;
    private int _activeExecutions;
    private int _queuedItems;
    private bool _disposed;

    private readonly int _maxParallelism;
    private readonly int _maxQueueLength;

    public ThreadPoolBulkheadStrategy()
        : this(maxParallelism: 10, maxQueueLength: 100)
    {
    }

    public ThreadPoolBulkheadStrategy(int maxParallelism, int maxQueueLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelism);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueueLength);
        _maxParallelism = maxParallelism;
        _maxQueueLength = maxQueueLength;
        _executionSemaphore = new SemaphoreSlim(maxParallelism, maxParallelism);
        _queueSemaphore = new SemaphoreSlim(maxQueueLength, maxQueueLength);
    }

    /// <summary>Releases semaphore resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _executionSemaphore.Dispose();
        _queueSemaphore.Dispose();
    }

    public override string StrategyId => "bulkhead-thread-pool";
    public override string StrategyName => "Thread Pool Bulkhead";
    public override string Category => "Bulkhead";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Thread Pool Bulkhead",
        Description = "Isolates operations using a dedicated thread pool with configurable parallelism and queue depth",
        Category = "Bulkhead",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current number of active executions.
    /// </summary>
    public int ActiveExecutions => _activeExecutions;

    /// <summary>
    /// Gets the current queue length.
    /// </summary>
    public int QueuedItems => _queuedItems;

    /// <summary>
    /// Gets the available execution slots.
    /// </summary>
    public int AvailableSlots => _executionSemaphore.CurrentCount;

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        // Try to enter the queue
        if (!await _queueSemaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new BulkheadRejectedException($"Bulkhead queue is full ({_maxQueueLength})"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata =
                {
                    ["queueLength"] = _queuedItems,
                    ["maxQueueLength"] = _maxQueueLength
                }
            };
        }

        Interlocked.Increment(ref _queuedItems);

        try
        {
            // Wait for execution slot
            await _executionSemaphore.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _queuedItems);
            Interlocked.Increment(ref _activeExecutions);

            try
            {
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    Metadata =
                    {
                        ["activeExecutions"] = _activeExecutions,
                        ["queuedItems"] = _queuedItems
                    }
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeExecutions);
                _executionSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new OperationCanceledException("Operation was cancelled while waiting in bulkhead queue"),
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
        finally
        {
            _queueSemaphore.Release();
        }
    }

    protected override string? GetCurrentState() =>
        $"Active: {_activeExecutions}/{_maxParallelism}, Queued: {_queuedItems}/{_maxQueueLength}";
}

/// <summary>
/// Semaphore-based bulkhead strategy for simple concurrency limiting.
/// </summary>
public sealed class SemaphoreBulkheadStrategy : ResilienceStrategyBase, IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _activeExecutions;
    private bool _disposed;

    private readonly int _maxParallelism;
    private readonly TimeSpan _waitTimeout;

    public SemaphoreBulkheadStrategy()
        : this(maxParallelism: 10, waitTimeout: TimeSpan.FromSeconds(30))
    {
    }

    public SemaphoreBulkheadStrategy(int maxParallelism, TimeSpan waitTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelism);
        if (waitTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitTimeout), "Wait timeout must be positive.");
        _maxParallelism = maxParallelism;
        _waitTimeout = waitTimeout;
        _semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);
    }

    /// <summary>Releases semaphore resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
    }

    public override string StrategyId => "bulkhead-semaphore";
    public override string StrategyName => "Semaphore Bulkhead";
    public override string Category => "Bulkhead";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Semaphore Bulkhead",
        Description = "Simple semaphore-based isolation with configurable wait timeout - lightweight and efficient",
        Category = "Bulkhead",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the current number of active executions.
    /// </summary>
    public int ActiveExecutions => _activeExecutions;

    /// <summary>
    /// Gets the available slots.
    /// </summary>
    public int AvailableSlots => _semaphore.CurrentCount;

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_waitTimeout);

        try
        {
            if (!await _semaphore.WaitAsync(_waitTimeout, cancellationToken))
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new BulkheadRejectedException($"Bulkhead is full and wait timeout exceeded ({_waitTimeout.TotalSeconds}s)"),
                    Attempts = 0,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    Metadata = { ["maxParallelism"] = _maxParallelism }
                };
            }

            Interlocked.Increment(ref _activeExecutions);

            try
            {
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    Metadata = { ["activeExecutions"] = _activeExecutions }
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeExecutions);
                _semaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new OperationCanceledException("Operation was cancelled while waiting for bulkhead"),
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

    protected override string? GetCurrentState() =>
        $"Active: {_activeExecutions}/{_maxParallelism}";
}

/// <summary>
/// Partition-based bulkhead strategy for isolating different operation types.
/// </summary>
public sealed class PartitionBulkheadStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _partitions = new();
    private readonly ConcurrentDictionary<string, int> _activeByPartition = new();
    private readonly int _defaultPartitionSize;
    private readonly Dictionary<string, int> _partitionSizes;

    public PartitionBulkheadStrategy()
        : this(defaultPartitionSize: 10, new Dictionary<string, int>())
    {
    }

    public PartitionBulkheadStrategy(int defaultPartitionSize, Dictionary<string, int> partitionSizes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(defaultPartitionSize);
        ArgumentNullException.ThrowIfNull(partitionSizes);
        _defaultPartitionSize = defaultPartitionSize;
        _partitionSizes = new Dictionary<string, int>(partitionSizes);
    }

    public override string StrategyId => "bulkhead-partition";
    public override string StrategyName => "Partition Bulkhead";
    public override string Category => "Bulkhead";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Partition Bulkhead",
        Description = "Isolates operations into partitions with independent concurrency limits - prevents one partition from affecting others",
        Category = "Bulkhead",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Configures a specific partition size.
    /// </summary>
    public void ConfigurePartition(string partitionKey, int maxParallelism)
    {
        _partitionSizes[partitionKey] = maxParallelism;

        // Update existing semaphore if it exists
        if (_partitions.TryGetValue(partitionKey, out var existing))
        {
            // Can't resize SemaphoreSlim, so we leave it - new config applies on next creation
        }
    }

    /// <summary>
    /// Gets active execution count for a partition.
    /// </summary>
    public int GetActiveExecutions(string partitionKey)
    {
        return _activeByPartition.TryGetValue(partitionKey, out var count) ? count : 0;
    }

    private SemaphoreSlim GetOrCreatePartition(string partitionKey)
    {
        return _partitions.GetOrAdd(partitionKey, key =>
        {
            var size = _partitionSizes.TryGetValue(key, out var configured) ? configured : _defaultPartitionSize;
            return new SemaphoreSlim(size, size);
        });
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var partitionKey = context?.Data.TryGetValue("PartitionKey", out var pk) == true
            ? pk?.ToString() ?? "default"
            : "default";

        var semaphore = GetOrCreatePartition(partitionKey);

        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new BulkheadRejectedException($"Partition '{partitionKey}' bulkhead is full"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["partition"] = partitionKey }
            };
        }

        _activeByPartition.AddOrUpdate(partitionKey, 1, (_, v) => v + 1);

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["partition"] = partitionKey,
                    ["activeInPartition"] = _activeByPartition.GetValueOrDefault(partitionKey, 0)
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["partition"] = partitionKey }
            };
        }
        finally
        {
            _activeByPartition.AddOrUpdate(partitionKey, 0, (_, v) => Math.Max(0, v - 1));
            semaphore.Release();
        }
    }

    protected override string? GetCurrentState()
    {
        var partitionInfo = string.Join(", ", _activeByPartition.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        return $"Partitions: [{partitionInfo}]";
    }
}

/// <summary>
/// Priority-based bulkhead strategy that prioritizes certain operations.
/// </summary>
public sealed class PriorityBulkheadStrategy : ResilienceStrategyBase
{
    private readonly SemaphoreSlim _highPrioritySemaphore;
    private readonly SemaphoreSlim _normalSemaphore;
    private int _highPriorityActive;
    private int _normalActive;

    private readonly int _highPrioritySlots;
    private readonly int _normalSlots;
    private readonly int _priorityThreshold;

    public PriorityBulkheadStrategy()
        : this(highPrioritySlots: 5, normalSlots: 10, priorityThreshold: 5)
    {
    }

    public PriorityBulkheadStrategy(int highPrioritySlots, int normalSlots, int priorityThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(highPrioritySlots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(normalSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(priorityThreshold);
        _highPrioritySlots = highPrioritySlots;
        _normalSlots = normalSlots;
        _priorityThreshold = priorityThreshold;
        _highPrioritySemaphore = new SemaphoreSlim(highPrioritySlots, highPrioritySlots);
        _normalSemaphore = new SemaphoreSlim(normalSlots, normalSlots);
    }

    public override string StrategyId => "bulkhead-priority";
    public override string StrategyName => "Priority Bulkhead";
    public override string Category => "Bulkhead";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Priority Bulkhead",
        Description = "Separates high-priority and normal operations into different bulkheads to ensure critical operations can proceed",
        Category = "Bulkhead",
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
        var priority = context?.Priority ?? 0;
        var isHighPriority = priority >= _priorityThreshold;

        var semaphore = isHighPriority ? _highPrioritySemaphore : _normalSemaphore;

        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            // For high priority, try to use normal slots as overflow
            if (isHighPriority && await _normalSemaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                semaphore = _normalSemaphore;
            }
            else
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new BulkheadRejectedException($"{(isHighPriority ? "High priority" : "Normal")} bulkhead is full"),
                    Attempts = 0,
                    TotalDuration = TimeSpan.Zero,
                    Metadata = { ["isHighPriority"] = isHighPriority }
                };
            }
        }

        if (isHighPriority)
            Interlocked.Increment(ref _highPriorityActive);
        else
            Interlocked.Increment(ref _normalActive);

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["isHighPriority"] = isHighPriority,
                    ["highPriorityActive"] = _highPriorityActive,
                    ["normalActive"] = _normalActive
                }
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
            if (isHighPriority)
                Interlocked.Decrement(ref _highPriorityActive);
            else
                Interlocked.Decrement(ref _normalActive);

            semaphore.Release();
        }
    }

    protected override string? GetCurrentState() =>
        $"High: {_highPriorityActive}/{_highPrioritySlots}, Normal: {_normalActive}/{_normalSlots}";
}

/// <summary>
/// Adaptive bulkhead strategy that adjusts capacity based on load.
/// </summary>
public sealed class AdaptiveBulkheadStrategy : ResilienceStrategyBase
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentQueue<(DateTimeOffset timestamp, TimeSpan latency)> _history = new();
    private int _currentCapacity;
    private int _activeExecutions;
    private DateTimeOffset _lastAdaptation;
    private readonly object _adaptLock = new();

    private readonly int _minCapacity;
    private readonly int _maxCapacity;
    private readonly int _baseCapacity;
    private readonly TimeSpan _targetLatency;
    private readonly TimeSpan _adaptationInterval;

    public AdaptiveBulkheadStrategy()
        : this(minCapacity: 5, maxCapacity: 50, baseCapacity: 20, targetLatency: TimeSpan.FromMilliseconds(500), adaptationInterval: TimeSpan.FromSeconds(10))
    {
    }

    public AdaptiveBulkheadStrategy(int minCapacity, int maxCapacity, int baseCapacity, TimeSpan targetLatency, TimeSpan adaptationInterval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCapacity);
        if (baseCapacity < minCapacity || baseCapacity > maxCapacity) throw new ArgumentOutOfRangeException(nameof(baseCapacity), "Base capacity must be between min and max.");
        if (minCapacity > maxCapacity) throw new ArgumentOutOfRangeException(nameof(minCapacity), "Min capacity must be <= max capacity.");
        if (targetLatency <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(targetLatency), "Target latency must be positive.");
        if (adaptationInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(adaptationInterval), "Adaptation interval must be positive.");
        _minCapacity = minCapacity;
        _maxCapacity = maxCapacity;
        _baseCapacity = baseCapacity;
        _currentCapacity = baseCapacity;
        _targetLatency = targetLatency;
        _adaptationInterval = adaptationInterval;
        _semaphore = new SemaphoreSlim(maxCapacity, maxCapacity);
        _lastAdaptation = DateTimeOffset.UtcNow;

        // Initially restrict to base capacity
        for (int i = 0; i < maxCapacity - baseCapacity; i++)
        {
            _semaphore.Wait();
        }
    }

    public override string StrategyId => "bulkhead-adaptive";
    public override string StrategyName => "Adaptive Bulkhead";
    public override string Category => "Bulkhead";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Adaptive Bulkhead",
        Description = "Self-adjusting bulkhead that increases capacity when latency is low and decreases when latency exceeds target",
        Category = "Bulkhead",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current capacity.
    /// </summary>
    public int CurrentCapacity
    {
        get
        {
            lock (_adaptLock)
            {
                return _currentCapacity;
            }
        }
    }

    private void Adapt()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAdaptation < _adaptationInterval) return;

        // Prune old entries
        var cutoff = now.AddMinutes(-1);
        while (_history.TryPeek(out var entry) && entry.timestamp < cutoff)
        {
            _history.TryDequeue(out _);
        }

        var entries = _history.ToArray();
        if (entries.Length < 10) return;

        var avgLatency = TimeSpan.FromMilliseconds(entries.Average(e => e.latency.TotalMilliseconds));
        var newCapacity = _currentCapacity;

        if (avgLatency < _targetLatency * 0.5)
        {
            // Latency is well below target - increase capacity
            newCapacity = Math.Min(_maxCapacity, _currentCapacity + 2);
        }
        else if (avgLatency > _targetLatency * 1.5)
        {
            // Latency is above target - decrease capacity
            newCapacity = Math.Max(_minCapacity, _currentCapacity - 2);
        }

        if (newCapacity != _currentCapacity)
        {
            var diff = newCapacity - _currentCapacity;
            if (diff > 0)
            {
                // Release additional permits
                _semaphore.Release(diff);
            }
            else
            {
                // Try to acquire permits to reduce capacity (non-blocking)
                for (int i = 0; i < -diff; i++)
                {
                    if (!_semaphore.Wait(0)) break;
                }
            }
            _currentCapacity = newCapacity;
        }

        _lastAdaptation = now;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_adaptLock)
        {
            Adapt();
        }

        if (!await _semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new BulkheadRejectedException($"Adaptive bulkhead is full (capacity: {_currentCapacity})"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["currentCapacity"] = _currentCapacity }
            };
        }

        Interlocked.Increment(ref _activeExecutions);

        try
        {
            var opStart = DateTimeOffset.UtcNow;
            var result = await operation(cancellationToken);
            var latency = DateTimeOffset.UtcNow - opStart;

            _history.Enqueue((DateTimeOffset.UtcNow, latency));

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["currentCapacity"] = _currentCapacity,
                    ["latencyMs"] = latency.TotalMilliseconds
                }
            };
        }
        catch (Exception ex)
        {
            _history.Enqueue((DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - startTime));

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
            Interlocked.Decrement(ref _activeExecutions);
            _semaphore.Release();
        }
    }

    protected override string? GetCurrentState() =>
        $"Capacity: {_currentCapacity} (range: {_minCapacity}-{_maxCapacity}), Active: {_activeExecutions}";
}

/// <summary>
/// Exception thrown when a bulkhead rejects a request.
/// </summary>
public sealed class BulkheadRejectedException : Exception
{
    public BulkheadRejectedException(string message) : base(message) { }
    public BulkheadRejectedException(string message, Exception innerException) : base(message, innerException) { }
}
