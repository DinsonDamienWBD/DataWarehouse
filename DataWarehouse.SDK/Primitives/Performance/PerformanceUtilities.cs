using System.Buffers;
using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Primitives.Performance;

/// <summary>
/// Generic object pool for reducing allocation overhead and improving performance.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
/// <remarks>
/// Object pools are useful for frequently created and destroyed objects, reducing
/// garbage collection pressure and improving overall application performance.
/// </remarks>
public sealed class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> _objects = new();
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly int _maxSize;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the ObjectPool class.
    /// </summary>
    /// <param name="factory">Factory function to create new instances.</param>
    /// <param name="reset">Optional action to reset an object before returning it to the pool.</param>
    /// <param name="maxSize">Maximum number of objects to keep in the pool. Default is 100.</param>
    public ObjectPool(Func<T> factory, Action<T>? reset = null, int maxSize = 100)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _reset = reset;
        _maxSize = maxSize;
    }

    /// <summary>
    /// Gets the current number of objects in the pool.
    /// </summary>
    public int Count => _objects.Count;

    /// <summary>
    /// Rents an object from the pool, or creates a new one if the pool is empty.
    /// </summary>
    /// <returns>An object from the pool.</returns>
    public T Rent()
    {
        if (_objects.TryTake(out var obj))
        {
            Interlocked.Decrement(ref _count);
            return obj;
        }

        return _factory();
    }

    /// <summary>
    /// Returns an object to the pool for reuse.
    /// </summary>
    /// <param name="obj">The object to return to the pool.</param>
    /// <remarks>
    /// If the pool is at maximum capacity, the object is not retained and will be garbage collected.
    /// </remarks>
    public void Return(T obj)
    {
        if (obj == null)
            return;

        _reset?.Invoke(obj);

        // Atomic check-and-increment to prevent exceeding _maxSize under concurrency
        int currentCount;
        do
        {
            currentCount = _count;
            if (currentCount >= _maxSize)
                return; // Pool full — discard object
        }
        while (Interlocked.CompareExchange(ref _count, currentCount + 1, currentCount) != currentCount);

        _objects.Add(obj);
    }

    /// <summary>
    /// Clears all objects from the pool.
    /// </summary>
    public void Clear()
    {
        while (_objects.TryTake(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }
}

/// <summary>
/// High-performance buffer using Span&lt;T&gt; for zero-allocation operations.
/// </summary>
/// <typeparam name="T">The type of elements in the buffer.</typeparam>
/// <remarks>
/// SpanBuffer provides a wrapper around ArrayPool for efficient, reusable buffers
/// with span-based access for maximum performance in tight loops and hot paths.
/// </remarks>
public sealed class SpanBuffer<T> : IDisposable
{
    private T[]? _buffer;
    private readonly int _size;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the SpanBuffer class.
    /// </summary>
    /// <param name="size">The size of the buffer to allocate.</param>
    public SpanBuffer(int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive.");

        _size = size;
        _buffer = ArrayPool<T>.Shared.Rent(size);
    }

    /// <summary>
    /// Gets the size of the buffer.
    /// </summary>
    public int Size => _size;

    /// <summary>
    /// Gets a span representing the buffer contents.
    /// </summary>
    /// <returns>A span over the buffer.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the buffer has been disposed.</exception>
    public Span<T> AsSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _buffer.AsSpan(0, _size);
    }

    /// <summary>
    /// Gets a memory region representing the buffer contents.
    /// </summary>
    /// <returns>A memory region over the buffer.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the buffer has been disposed.</exception>
    public Memory<T> AsMemory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _buffer.AsMemory(0, _size);
    }

    /// <summary>
    /// Copies data into the buffer.
    /// </summary>
    /// <param name="source">The source span to copy from.</param>
    /// <exception cref="ArgumentException">Thrown if source is larger than buffer capacity.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the buffer has been disposed.</exception>
    public void CopyFrom(ReadOnlySpan<T> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (source.Length > _size)
            throw new ArgumentException("Source is larger than buffer capacity.", nameof(source));

        source.CopyTo(AsSpan());
    }

    /// <summary>
    /// Disposes the buffer and returns it to the pool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_buffer != null)
        {
            ArrayPool<T>.Shared.Return(_buffer, clearArray: true);
            _buffer = null;
        }

        _disposed = true;
    }
}

/// <summary>
/// Batches operations for efficient bulk processing with configurable batch sizes and delays.
/// </summary>
/// <typeparam name="T">The type of items to batch.</typeparam>
/// <remarks>
/// BatchProcessor is useful for scenarios where processing items individually is inefficient,
/// such as database inserts, network requests, or I/O operations. It automatically batches
/// items based on size or time thresholds.
/// </remarks>
public sealed class BatchProcessor<T> : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _processBatch;
    private readonly int _maxBatchSize;
    private readonly TimeSpan _maxDelay;
    private readonly List<T> _currentBatch = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the BatchProcessor class.
    /// </summary>
    /// <param name="processBatch">Function to process a batch of items.</param>
    /// <param name="maxBatchSize">Maximum number of items per batch. Default is 100.</param>
    /// <param name="maxDelay">Maximum time to wait before processing a partial batch. Default is 1 second.</param>
    public BatchProcessor(
        Func<IReadOnlyList<T>, CancellationToken, Task> processBatch,
        int maxBatchSize = 100,
        TimeSpan? maxDelay = null)
    {
        _processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));
        _maxBatchSize = maxBatchSize > 0 ? maxBatchSize : throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(1);

        _flushTask = Task.Run(FlushPeriodicallyAsync);
    }

    /// <summary>
    /// Gets the current batch count.
    /// </summary>
    public int CurrentBatchCount { get { try { return _currentBatch.Count; } catch { return 0; } } }

    /// <summary>
    /// Adds an item to the batch for processing.
    /// </summary>
    /// <param name="item">The item to add.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task AddAsync(T item, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _currentBatch.Add(item);

            if (_currentBatch.Count >= _maxBatchSize)
            {
                await FlushInternalAsync(cancellationToken);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Manually flushes the current batch, processing all pending items.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await FlushInternalAsync(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task FlushInternalAsync(CancellationToken cancellationToken)
    {
        if (_currentBatch.Count == 0)
            return;

        var batch = _currentBatch.ToList();
        _currentBatch.Clear();

        await _processBatch(batch, cancellationToken);
    }

    private async Task FlushPeriodicallyAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_maxDelay, _cts.Token);
                await FlushAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Disposes the batch processor and flushes any pending items.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try
        {
            await _flushTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        try
        {
            await FlushAsync();
        }
        finally
        {
            _cts.Dispose();
            _semaphore.Dispose();
        }
    }
}
