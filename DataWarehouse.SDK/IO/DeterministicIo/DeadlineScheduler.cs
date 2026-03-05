using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.IO.DeterministicIo;

/// <summary>
/// Exception thrown when an I/O operation exceeds its deadline or WCET bound.
/// </summary>
public sealed class DeadlineMissException : Exception
{
    /// <summary>Unique identifier of the operation that missed its deadline.</summary>
    public string OperationId { get; }

    /// <summary>Actual execution time.</summary>
    public TimeSpan Actual { get; }

    /// <summary>Maximum allowed execution time.</summary>
    public TimeSpan MaxAllowed { get; }

    public DeadlineMissException(string operationId, TimeSpan actual, TimeSpan maxAllowed)
        : base($"Deadline miss for operation '{operationId}': " +
               $"actual {actual.TotalMilliseconds:F3}ms exceeds max {maxAllowed.TotalMilliseconds:F3}ms.")
    {
        OperationId = operationId;
        Actual = actual;
        MaxAllowed = maxAllowed;
    }
}

/// <summary>
/// An I/O operation scheduled for EDF (Earliest Deadline First) execution.
/// </summary>
/// <param name="OperationId">Unique identifier (GUID) for tracing.</param>
/// <param name="Deadline">Deadline and priority metadata.</param>
/// <param name="Execute">The I/O work to perform with a rented buffer.</param>
/// <param name="Completion">Task completion source signaled when operation finishes.</param>
/// <param name="SubmittedAt">Timestamp when the operation was submitted.</param>
public sealed record ScheduledIoOperation(
    string OperationId,
    IoDeadline Deadline,
    Func<PooledBuffer, ValueTask> Execute,
    TaskCompletionSource<bool> Completion,
    DateTimeOffset SubmittedAt);

/// <summary>
/// Runtime statistics for the deadline scheduler.
/// </summary>
/// <param name="TotalScheduled">Total operations submitted.</param>
/// <param name="TotalCompleted">Total operations completed successfully.</param>
/// <param name="DeadlineMisses">Number of operations that missed their deadline.</param>
/// <param name="P99LatencyUs">99th percentile latency in microseconds.</param>
/// <param name="MaxLatencyUs">Maximum observed latency in microseconds.</param>
/// <param name="CurrentQueueDepth">Number of operations waiting in queue.</param>
/// <param name="BuffersInUse">Number of buffers currently rented from pool.</param>
public sealed record SchedulerStats(
    long TotalScheduled,
    long TotalCompleted,
    long DeadlineMisses,
    double P99LatencyUs,
    double MaxLatencyUs,
    int CurrentQueueDepth,
    int BuffersInUse);

/// <summary>
/// Earliest Deadline First (EDF) I/O scheduler for deterministic bounded-latency operations.
/// Runs on a dedicated high-priority thread to avoid ThreadPool starvation.
/// Uses pre-allocated latency tracking (no dynamic allocation on hot path).
/// </summary>
/// <remarks>
/// Thread safety:
/// <list type="bullet">
///   <item><see cref="PriorityQueue{TElement,TPriority}"/> protected by <see cref="SemaphoreSlim"/>(1,1)</item>
///   <item>Dedicated processing thread avoids ThreadPool contention</item>
///   <item>Latency tracking uses pre-allocated circular buffer</item>
///   <item>All counters use <see cref="Interlocked"/> operations</item>
/// </list>
/// </remarks>
public sealed class DeadlineScheduler : IDisposable
{
    private const int LatencyHistorySize = 10_000;

    private readonly PreAllocatedBufferPool _bufferPool;
    private readonly DeterministicIoConfig _config;
    private readonly ILogger _logger;

    // EDF priority queue: ordered by absolute deadline (earliest first)
    private readonly PriorityQueue<ScheduledIoOperation, DateTimeOffset> _queue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly SemaphoreSlim _workAvailable = new(0, int.MaxValue);

    // Dedicated processing thread
    private readonly Thread _processingThread;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed; // 0=not disposed, 1=disposed (Interlocked)

    // Pre-allocated latency tracking (circular buffer, no List, no allocation)
    private readonly long[] _latencyHistoryUs = new long[LatencyHistorySize];
    private volatile int _latencyWriteIndex;
    private int _latencyCount;

    // Counters (Interlocked for thread safety)
    private long _totalScheduled;
    private long _totalCompleted;
    private long _deadlineMisses;
    private long _maxLatencyUs;
    private int _currentQueueDepth;

    /// <summary>
    /// Creates a new EDF deadline scheduler.
    /// </summary>
    /// <param name="bufferPool">Pre-allocated buffer pool for I/O operations.</param>
    /// <param name="config">Deterministic I/O configuration.</param>
    /// <param name="logger">Optional logger for deadline miss warnings.</param>
    public DeadlineScheduler(PreAllocatedBufferPool bufferPool, DeterministicIoConfig config, ILogger? logger = null)
    {
        _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? NullLogger.Instance;

        _processingThread = new Thread(ProcessingLoop)
        {
            Name = "DeterministicIo-EDF-Scheduler",
            IsBackground = false,
            Priority = ThreadPriority.AboveNormal
        };
        _processingThread.Start();
    }

    /// <summary>
    /// Schedules an I/O operation for EDF execution.
    /// If the deadline has already passed, the operation is executed immediately or fails
    /// based on <see cref="DeterministicIoConfig.FailOnDeadlineMiss"/>.
    /// </summary>
    /// <param name="operation">The operation to schedule.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the operation was scheduled or executed; false if deadline already passed and FailOnDeadlineMiss is set.</returns>
    public async ValueTask<bool> ScheduleAsync(ScheduledIoOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Interlocked.Increment(ref _totalScheduled);

        // Check if deadline already passed
        if (operation.Deadline.Deadline <= DateTimeOffset.UtcNow)
        {
            if (_config.FailOnDeadlineMiss)
            {
                Interlocked.Increment(ref _deadlineMisses);
                operation.Completion.TrySetException(
                    new DeadlineMissException(operation.OperationId, TimeSpan.Zero, operation.Deadline.MaxLatency));
                return false;
            }

            // Execute immediately despite missed deadline (logging mode)
            _logger.LogWarning(
                "Operation {OperationId} submitted with already-passed deadline. Executing immediately.",
                operation.OperationId);
        }

        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _queue.Enqueue(operation, operation.Deadline.Deadline);
            Interlocked.Increment(ref _currentQueueDepth);
        }
        finally
        {
            _queueLock.Release();
        }

        // Signal the processing thread
        _workAvailable.Release();
        return true;
    }

    /// <summary>
    /// Gets current scheduler statistics.
    /// </summary>
    public SchedulerStats GetStats()
    {
        return new SchedulerStats(
            TotalScheduled: Interlocked.Read(ref _totalScheduled),
            TotalCompleted: Interlocked.Read(ref _totalCompleted),
            DeadlineMisses: Interlocked.Read(ref _deadlineMisses),
            P99LatencyUs: ComputeP99(),
            MaxLatencyUs: Interlocked.Read(ref _maxLatencyUs),
            CurrentQueueDepth: Volatile.Read(ref _currentQueueDepth),
            BuffersInUse: _bufferPool.Total - _bufferPool.Available);
    }

    /// <summary>
    /// Initiates graceful shutdown: drains the queue and completes all pending operations.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdownCts.Cancel();

        // Release the semaphore to unblock the processing thread
        try { _workAvailable.Release(); } catch (SemaphoreFullException) { }

        // Wait for processing thread to finish (bounded)
        _processingThread.Join(TimeSpan.FromSeconds(5));

        // Fail any remaining operations
        _queueLock.Wait();
        try
        {
            while (_queue.TryDequeue(out var op, out _))
            {
                op.Completion.TrySetCanceled();
            }
        }
        finally
        {
            _queueLock.Release();
        }

        _queueLock.Dispose();
        _workAvailable.Dispose();
        _shutdownCts.Dispose();
    }

    private void ProcessingLoop()
    {
        var ct = _shutdownCts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for work with timeout to allow shutdown checks
                if (!_workAvailable.Wait(TimeSpan.FromMilliseconds(50), ct))
                    continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Dequeue highest-priority (earliest deadline) operation
            ScheduledIoOperation? operation = null;
            _queueLock.Wait(ct);
            try
            {
                if (_queue.TryDequeue(out var op, out _))
                {
                    operation = op;
                    Interlocked.Decrement(ref _currentQueueDepth);
                }
            }
            finally
            {
                _queueLock.Release();
            }

            if (operation is null)
                continue;

            ExecuteOperation(operation);
        }

        // Drain remaining operations on shutdown
        DrainQueue();
    }

    private void ExecuteOperation(ScheduledIoOperation operation)
    {
        var sw = Stopwatch.StartNew();

        // Try to rent a buffer from pre-allocated pool
        if (!_bufferPool.TryRent(out var buffer))
        {
            // Buffer exhaustion is a deadline miss
            sw.Stop();
            Interlocked.Increment(ref _deadlineMisses);
            _logger.LogError(
                "Buffer pool exhausted for operation {OperationId}. Deadline miss.",
                operation.OperationId);

            if (_config.FailOnDeadlineMiss)
            {
                operation.Completion.TrySetException(
                    new DeadlineMissException(operation.OperationId, sw.Elapsed, operation.Deadline.MaxLatency));
            }
            else
            {
                operation.Completion.TrySetResult(false);
            }
            return;
        }

        try
        {
            // Execute the I/O operation synchronously on the dedicated thread
            var vt = operation.Execute(buffer);
            if (!vt.IsCompleted)
                vt.AsTask().GetAwaiter().GetResult();
            sw.Stop();

            var latencyUs = sw.Elapsed.TotalMicroseconds;

            // Check deadline
            bool deadlineMissed = DateTimeOffset.UtcNow > operation.Deadline.Deadline;
            bool wcetExceeded = _config.EnableWcetEnforcement &&
                                sw.Elapsed > operation.Deadline.MaxLatency;

            if (deadlineMissed || wcetExceeded)
            {
                Interlocked.Increment(ref _deadlineMisses);
                _logger.LogWarning(
                    "Deadline miss for operation {OperationId}: latency {LatencyMs:F3}ms, " +
                    "max allowed {MaxMs:F3}ms, deadline passed: {DeadlinePassed}",
                    operation.OperationId,
                    sw.Elapsed.TotalMilliseconds,
                    operation.Deadline.MaxLatency.TotalMilliseconds,
                    deadlineMissed);

                if (_config.FailOnDeadlineMiss)
                {
                    operation.Completion.TrySetException(
                        new DeadlineMissException(operation.OperationId, sw.Elapsed, operation.Deadline.MaxLatency));
                    return;
                }
            }

            // Record latency (pre-allocated circular buffer, no allocation)
            RecordLatency((long)latencyUs);

            Interlocked.Increment(ref _totalCompleted);
            operation.Completion.TrySetResult(true);
        }
        catch (Exception ex) when (ex is not DeadlineMissException)
        {
            sw.Stop();
            _logger.LogError(ex, "I/O operation {OperationId} failed.", operation.OperationId);
            operation.Completion.TrySetException(ex);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private void DrainQueue()
    {
        _queueLock.Wait();
        try
        {
            while (_queue.TryDequeue(out var op, out _))
            {
                Interlocked.Decrement(ref _currentQueueDepth);
                ExecuteOperation(op);
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private void RecordLatency(long latencyUs)
    {
        // Update max (lock-free CAS loop)
        long currentMax;
        do
        {
            currentMax = Interlocked.Read(ref _maxLatencyUs);
            if (latencyUs <= currentMax) break;
        } while (Interlocked.CompareExchange(ref _maxLatencyUs, latencyUs, currentMax) != currentMax);

        // Write to circular buffer (single-writer on dedicated thread, no lock needed)
        int index = _latencyWriteIndex;
        _latencyHistoryUs[index] = latencyUs;
        _latencyWriteIndex = (index + 1) % LatencyHistorySize;
        if (_latencyCount < LatencyHistorySize)
            Interlocked.Increment(ref _latencyCount);
    }

    private double ComputeP99()
    {
        int count = Volatile.Read(ref _latencyCount);
        if (count == 0) return 0.0;

        // Copy current samples for sorting (pre-allocated would be ideal but
        // GetStats is not on the hot path — this is acceptable)
        int sampleCount = Math.Min(count, LatencyHistorySize);
        var samples = new long[sampleCount];
        Array.Copy(_latencyHistoryUs, samples, sampleCount);
        Array.Sort(samples);

        int p99Index = (int)(sampleCount * 0.99);
        if (p99Index >= sampleCount) p99Index = sampleCount - 1;
        return samples[p99Index];
    }
}
