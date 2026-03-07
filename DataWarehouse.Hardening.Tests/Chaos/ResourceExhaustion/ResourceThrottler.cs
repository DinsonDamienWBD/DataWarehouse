using System.Buffers;
using System.Collections.Concurrent;

namespace DataWarehouse.Hardening.Tests.Chaos.ResourceExhaustion;

/// <summary>
/// Utility class that simulates resource starvation conditions for chaos testing.
/// Provides ThreadPool starvation, MemoryPool exhaustion, and disk-full simulation.
/// All methods return IDisposable handles -- cleanup releases resources so tests
/// don't poison each other. Thread-safe with proper cleanup even if test throws.
///
/// Report: "Stage 3 - Steps 3-4 - Resource Exhaustion"
/// </summary>
public static class ResourceThrottler
{
    /// <summary>
    /// Saturates all ThreadPool threads with blocking work items.
    /// Uses ManualResetEventSlim to hold threads until disposed.
    /// Returns an IDisposable that releases all blocked threads on dispose.
    /// </summary>
    /// <param name="extraItems">Additional work items beyond min threads to ensure full saturation.</param>
    /// <returns>Disposable handle that releases blocked threads.</returns>
    public static IDisposable StarveThreadPool(int extraItems = 64)
    {
        return new ThreadPoolStarvation(extraItems);
    }

    /// <summary>
    /// Rents all available buffers from a MemoryPool until allocation fails.
    /// Returns an IDisposable that returns all rented buffers on dispose.
    /// </summary>
    /// <param name="bufferSize">Size of each buffer to rent.</param>
    /// <param name="maxAllocations">Maximum number of allocations to attempt.</param>
    /// <returns>Disposable handle that returns all rented buffers.</returns>
    public static IDisposable ExhaustMemoryPool(int bufferSize = 1024 * 1024, int maxAllocations = 512)
    {
        return new MemoryPoolExhaustion(bufferSize, maxAllocations);
    }

    /// <summary>
    /// Wraps a stream so that writes throw IOException after a configurable byte threshold,
    /// simulating a disk-full condition. The underlying stream can be replaced/reset to
    /// simulate freeing disk space.
    /// </summary>
    /// <param name="backingStream">The actual stream to write to (until threshold).</param>
    /// <param name="bytesBeforeFull">Number of bytes allowed before disk-full error.</param>
    /// <returns>A DiskFullStream that throws IOException when the threshold is exceeded.</returns>
    public static DiskFullStream SimulateDiskFull(Stream backingStream, long bytesBeforeFull)
    {
        return new DiskFullStream(backingStream, bytesBeforeFull);
    }

    /// <summary>
    /// Holds ThreadPool threads hostage by queuing blocking work items.
    /// </summary>
    private sealed class ThreadPoolStarvation : IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private readonly CountdownEvent _allStarted;
        private volatile bool _disposed;
        private readonly int _totalItems;

        public ThreadPoolStarvation(int extraItems)
        {
            // Get current min threads so we queue enough to saturate
            ThreadPool.GetMinThreads(out int minWorker, out _);
            _totalItems = minWorker + extraItems;
            _allStarted = new CountdownEvent(_totalItems);

            for (int i = 0; i < _totalItems; i++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _allStarted.Signal();
                        // Block this thread until disposed
                        _gate.Wait(TimeSpan.FromMinutes(2));
                    }
                    catch (ObjectDisposedException)
                    {
                        // Gate was disposed -- exit cleanly
                    }
                });
            }

            // Wait briefly for threads to actually start blocking
            // Don't wait forever -- some items may queue if pool is already at capacity
            _allStarted.Wait(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Release all blocked threads
            _gate.Set();

            // Give threads a moment to unblock
            Thread.Sleep(100);

            _gate.Dispose();
            _allStarted.Dispose();
        }
    }

    /// <summary>
    /// Allocates large byte arrays to consume available memory,
    /// simulating MemoryPool exhaustion.
    /// </summary>
    private sealed class MemoryPoolExhaustion : IDisposable
    {
        private readonly ConcurrentBag<byte[]> _allocations = new();
        private volatile bool _disposed;

        public MemoryPoolExhaustion(int bufferSize, int maxAllocations)
        {
            for (int i = 0; i < maxAllocations; i++)
            {
                try
                {
                    var buffer = new byte[bufferSize];
                    // Touch the buffer to ensure it's committed in physical memory
                    buffer[0] = 0xFF;
                    buffer[bufferSize - 1] = 0xFF;
                    _allocations.Add(buffer);
                }
                catch (OutOfMemoryException)
                {
                    // We've exhausted available memory -- stop allocating
                    break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Clear references so GC can reclaim
            while (_allocations.TryTake(out _)) { }

            // Force a collection to reclaim memory
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }
    }
}

/// <summary>
/// A stream wrapper that throws IOException("No space left on device") after a configured
/// byte threshold has been written, simulating disk-full conditions.
/// Reads pass through to the backing stream. Space can be "freed" by calling ResetBytesWritten.
/// </summary>
public sealed class DiskFullStream : Stream
{
    private readonly Stream _backing;
    private long _bytesWritten;
    private long _bytesBeforeFull;
    private volatile bool _diskFull;
    private bool _disposed;

    /// <summary>Gets the total bytes written through this stream.</summary>
    public long TotalBytesWritten => Volatile.Read(ref _bytesWritten);

    /// <summary>Gets whether the stream is currently simulating disk-full.</summary>
    public bool IsDiskFull => _diskFull;

    internal DiskFullStream(Stream backing, long bytesBeforeFull)
    {
        _backing = backing ?? throw new ArgumentNullException(nameof(backing));
        _bytesBeforeFull = bytesBeforeFull;
    }

    /// <summary>
    /// Simulate "freeing disk space" by resetting the byte counter and threshold.
    /// After this call, writes will succeed again up to the new threshold.
    /// </summary>
    /// <param name="newThreshold">New byte threshold before next disk-full. -1 means unlimited.</param>
    public void FreeSpace(long newThreshold = -1)
    {
        Interlocked.Exchange(ref _bytesWritten, 0);
        _bytesBeforeFull = newThreshold < 0 ? long.MaxValue : newThreshold;
        _diskFull = false;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDiskFull(count);
        _backing.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDiskFull(buffer.Length);
        _backing.Write(buffer);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ThrowIfDiskFull(count);
        await _backing.WriteAsync(buffer, offset, count, ct);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDiskFull(buffer.Length);
        await _backing.WriteAsync(buffer, ct);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        ThrowIfDiskFull(1);
        _backing.WriteByte(value);
        Interlocked.Increment(ref _bytesWritten);
    }

    public override void Flush() => _backing.Flush();
    public override Task FlushAsync(CancellationToken ct) => _backing.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => _backing.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _backing.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _backing.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => _backing.Seek(offset, origin);
    public override void SetLength(long value) => _backing.SetLength(value);
    public override bool CanRead => _backing.CanRead;
    public override bool CanSeek => _backing.CanSeek;
    public override bool CanWrite => !_diskFull && _backing.CanWrite;
    public override long Length => _backing.Length;
    public override long Position
    {
        get => _backing.Position;
        set => _backing.Position = value;
    }

    private void ThrowIfDiskFull(int bytesToWrite)
    {
        long current = Volatile.Read(ref _bytesWritten);
        if (current + bytesToWrite > _bytesBeforeFull)
        {
            _diskFull = true;
            throw new IOException("No space left on device");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _backing.Dispose();
        }
        base.Dispose(disposing);
    }
}
