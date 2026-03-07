using System.Diagnostics;

namespace DataWarehouse.Hardening.Tests.Chaos.ClockSkew;

/// <summary>
/// A TimeProvider that can be skewed at runtime to simulate clock manipulation.
/// Supports arbitrary offsets (positive = future, negative = past) with thread-safe
/// runtime adjustment. Tracks all calls for test assertions.
///
/// Usage: inject into any component that depends on time to verify it handles
/// extreme clock manipulation without security bypass, crashes, or incorrect behavior.
/// </summary>
public sealed class SkewableTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private TimeSpan _offset;
    private long _getUtcNowCallCount;
    private long _getTimestampCallCount;
    private DateTimeOffset _minReturnedTime = DateTimeOffset.MaxValue;
    private DateTimeOffset _maxReturnedTime = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new SkewableTimeProvider with the specified offset.
    /// </summary>
    /// <param name="offset">Initial offset. Positive = future, negative = past.</param>
    public SkewableTimeProvider(TimeSpan offset)
    {
        _offset = offset;
    }

    /// <summary>
    /// Initializes a new SkewableTimeProvider with zero offset.
    /// </summary>
    public SkewableTimeProvider() : this(TimeSpan.Zero)
    {
    }

    /// <summary>
    /// Gets the current offset being applied to wall clock time.
    /// </summary>
    public TimeSpan CurrentOffset
    {
        get { lock (_lock) return _offset; }
    }

    /// <summary>
    /// Number of times GetUtcNow() has been called.
    /// </summary>
    public long GetUtcNowCallCount => Interlocked.Read(ref _getUtcNowCallCount);

    /// <summary>
    /// Number of times GetTimestamp() has been called.
    /// </summary>
    public long GetTimestampCallCount => Interlocked.Read(ref _getTimestampCallCount);

    /// <summary>
    /// The minimum time ever returned by GetUtcNow().
    /// </summary>
    public DateTimeOffset MinReturnedTime
    {
        get { lock (_lock) return _minReturnedTime; }
    }

    /// <summary>
    /// The maximum time ever returned by GetUtcNow().
    /// </summary>
    public DateTimeOffset MaxReturnedTime
    {
        get { lock (_lock) return _maxReturnedTime; }
    }

    /// <summary>
    /// Sets the offset to a specific value. Thread-safe.
    /// </summary>
    public void SetOffset(TimeSpan offset)
    {
        lock (_lock) _offset = offset;
    }

    /// <summary>
    /// Adjusts the offset forward by the specified amount.
    /// </summary>
    public void JumpForward(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "Use JumpBackward for negative adjustments.");
        lock (_lock) _offset += amount;
    }

    /// <summary>
    /// Adjusts the offset backward by the specified amount.
    /// </summary>
    public void JumpBackward(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "Use JumpForward for positive adjustments.");
        lock (_lock) _offset -= amount;
    }

    /// <summary>
    /// Returns the current UTC time plus the configured offset.
    /// </summary>
    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref _getUtcNowCallCount);

        TimeSpan currentOffset;
        lock (_lock) currentOffset = _offset;

        var result = DateTimeOffset.UtcNow + currentOffset;

        lock (_lock)
        {
            if (result < _minReturnedTime) _minReturnedTime = result;
            if (result > _maxReturnedTime) _maxReturnedTime = result;
        }

        return result;
    }

    /// <summary>
    /// Returns the current timestamp. Uses Stopwatch (monotonic) but adjusted
    /// for the offset to simulate time passage in timestamp-based calculations.
    /// </summary>
    public override long GetTimestamp()
    {
        Interlocked.Increment(ref _getTimestampCallCount);
        // Monotonic base + offset converted to ticks at Stopwatch frequency
        return Stopwatch.GetTimestamp()
               + (long)(_offset.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// Returns elapsed time between two timestamps.
    /// Uses the standard Stopwatch frequency for conversion.
    /// Note: GetElapsedTime is not virtual in .NET 10, so this is a convenience method
    /// with a different name for test code that needs elapsed time calculation.
    /// </summary>
    public TimeSpan ComputeElapsedTime(long startingTimestamp, long endingTimestamp)
    {
        long ticks = endingTimestamp - startingTimestamp;
        double seconds = (double)ticks / Stopwatch.Frequency;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Resets all tracking counters and min/max times.
    /// </summary>
    public void ResetTracking()
    {
        Interlocked.Exchange(ref _getUtcNowCallCount, 0);
        Interlocked.Exchange(ref _getTimestampCallCount, 0);
        lock (_lock)
        {
            _minReturnedTime = DateTimeOffset.MaxValue;
            _maxReturnedTime = DateTimeOffset.MinValue;
        }
    }
}
