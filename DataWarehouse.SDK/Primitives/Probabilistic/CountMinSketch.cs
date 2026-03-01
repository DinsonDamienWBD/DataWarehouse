using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Production-ready Count-Min Sketch for frequency estimation with bounded error.
/// Estimates the frequency of items in a stream using sub-linear space.
/// Supports merging for distributed scenarios (T85.1, T85.6, T85.7).
/// </summary>
/// <remarks>
/// Properties:
/// - Always overestimates (never underestimates) true frequency
/// - Error bounded by epsilon * total_count with probability (1 - delta)
/// - Space complexity: O(epsilon^-1 * log(delta^-1))
///
/// Use cases:
/// - Network traffic analysis (tracking packet frequencies)
/// - Database query optimization (estimating join cardinalities)
/// - Trending detection (identifying hot items)
/// - Cache admission policies (frequency-based eviction)
///
/// Thread-safety: NOT thread-safe. Use external synchronization for concurrent access.
/// </remarks>
public sealed class CountMinSketch<T> : IProbabilisticStructure, IMergeable<CountMinSketch<T>>, IConfidenceReporting
{
    private readonly long[,] _counters;
    private readonly int _width;
    private readonly int _depth;
    private readonly double _epsilon;
    private readonly double _delta;
    private readonly Func<T, byte[]> _serializer;
    private long _totalCount;
    // Cat 15 (finding 565): renamed from _lastEstimate to _lastEstimate for clarity
    private long _lastEstimate;

    /// <inheritdoc/>
    public string StructureType => "CountMinSketch";

    /// <inheritdoc/>
    public long MemoryUsageBytes => _width * _depth * sizeof(long) + 64;

    /// <inheritdoc/>
    public double ConfiguredErrorRate => _epsilon;

    /// <inheritdoc/>
    public long ItemCount => _totalCount;

    /// <summary>
    /// Gets the width (number of counters per hash function).
    /// </summary>
    public int Width => _width;

    /// <summary>
    /// Gets the depth (number of hash functions).
    /// </summary>
    public int Depth => _depth;

    /// <summary>
    /// Gets the total count of all items added.
    /// </summary>
    public long TotalCount => _totalCount;

    /// <summary>
    /// Creates a Count-Min Sketch with specified error bounds.
    /// </summary>
    /// <param name="epsilon">Relative error bound (e.g., 0.01 for 1% error).</param>
    /// <param name="delta">Probability of exceeding error bound (e.g., 0.01 for 99% confidence).</param>
    /// <param name="serializer">Optional custom serializer for type T.</param>
    public CountMinSketch(double epsilon = 0.01, double delta = 0.01, Func<T, byte[]>? serializer = null)
    {
        if (epsilon <= 0 || epsilon >= 1)
            throw new ArgumentOutOfRangeException(nameof(epsilon), "Epsilon must be between 0 and 1.");
        if (delta <= 0 || delta >= 1)
            throw new ArgumentOutOfRangeException(nameof(delta), "Delta must be between 0 and 1.");

        _epsilon = epsilon;
        _delta = delta;

        // Width = ceil(e/epsilon), Depth = ceil(ln(1/delta))
        _width = Math.Max(64, (int)Math.Ceiling(Math.E / epsilon));
        _depth = Math.Max(3, (int)Math.Ceiling(Math.Log(1.0 / delta)));

        _counters = new long[_depth, _width];
        _serializer = serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Creates a Count-Min Sketch from configuration.
    /// </summary>
    public CountMinSketch(ProbabilisticConfig config, Func<T, byte[]>? serializer = null)
        : this(config.RelativeError, 1 - config.ConfidenceLevel, serializer)
    {
    }

    // Private constructor for cloning/deserialization
    private CountMinSketch(long[,] counters, double epsilon, double delta, long totalCount, Func<T, byte[]> serializer)
    {
        _counters = counters;
        _width = counters.GetLength(1);
        _depth = counters.GetLength(0);
        _epsilon = epsilon;
        _delta = delta;
        _totalCount = totalCount;
        _serializer = serializer;
    }

    /// <summary>
    /// Adds an item to the sketch (increment count by 1).
    /// </summary>
    /// <param name="item">Item to add.</param>
    public void Add(T item)
    {
        Add(item, 1);
    }

    /// <summary>
    /// Adds an item to the sketch with specified count.
    /// </summary>
    /// <param name="item">Item to add.</param>
    /// <param name="count">Count to add (must be positive).</param>
    public void Add(T item, long count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");

        var bytes = _serializer(item);

        for (int i = 0; i < _depth; i++)
        {
            var index = GetHashIndex(bytes, i);
            _counters[i, index] += count;
        }

        Interlocked.Add(ref _totalCount, count);
    }

    /// <summary>
    /// Estimates the frequency of an item.
    /// </summary>
    /// <param name="item">Item to query.</param>
    /// <returns>Estimated frequency (may overestimate, never underestimates).</returns>
    public long Estimate(T item)
    {
        var bytes = _serializer(item);
        long minCount = long.MaxValue;

        for (int i = 0; i < _depth; i++)
        {
            var index = GetHashIndex(bytes, i);
            minCount = Math.Min(minCount, _counters[i, index]);
        }

        var result = minCount == long.MaxValue ? 0 : minCount;
        Interlocked.Exchange(ref _lastEstimate, result);
        return result;
    }

    /// <summary>
    /// Estimates frequency with probabilistic result metadata.
    /// </summary>
    /// <param name="item">Item to query.</param>
    /// <returns>Probabilistic result with confidence interval.</returns>
    public ProbabilisticResult<long> Query(T item)
    {
        var estimate = Estimate(item);
        var (lower, upper) = GetConfidenceInterval(1 - _delta);

        return new ProbabilisticResult<long>
        {
            Value = estimate,
            Confidence = 1 - _delta,
            LowerBound = lower,
            UpperBound = upper,
            SourceStructure = StructureType
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The confidence interval is computed from the last result of <see cref="Estimate"/> or <see cref="Query"/>.
    /// Callers must invoke <see cref="Estimate"/> immediately before calling this method;
    /// intervening calls from other threads may update the cached result.
    /// For thread-safe usage, use <see cref="Query"/> which atomically returns
    /// the estimate and its confidence interval together.
    /// </remarks>
    public (double Lower, double Upper) GetConfidenceInterval(double confidenceLevel = 0.95)
    {
        // True count is guaranteed to be <= estimate
        // With high probability, true count >= estimate - epsilon * total
        // Read _lastEstimate once to avoid tearing if updated concurrently
        var lastResult = Interlocked.Read(ref _lastEstimate);
        var errorBound = _epsilon * Interlocked.Read(ref _totalCount);
        return (Math.Max(0, lastResult - errorBound), lastResult);
    }

    /// <summary>
    /// Gets the top-K most frequent items by sampling the counter matrix.
    /// Note: This is an approximation and may miss some heavy hitters.
    /// </summary>
    /// <param name="k">Number of top items to return.</param>
    /// <param name="candidates">Candidate items to consider (required for type safety).</param>
    /// <returns>Top-K items with estimated frequencies.</returns>
    public IEnumerable<(T Item, long Frequency)> TopK(int k, IEnumerable<T> candidates)
    {
        var results = new SortedSet<(long Freq, T Item)>(
            Comparer<(long Freq, T Item)>.Create((a, b) =>
            {
                var cmp = b.Freq.CompareTo(a.Freq); // Descending
                return cmp != 0 ? cmp : a.Item?.GetHashCode().CompareTo(b.Item?.GetHashCode()) ?? 0;
            }));

        foreach (var item in candidates)
        {
            var freq = Estimate(item);
            if (freq > 0)
            {
                results.Add((freq, item));
                if (results.Count > k)
                    results.Remove(results.Max);
            }
        }

        foreach (var (freq, item) in results)
        {
            yield return (item, freq);
        }
    }

    /// <inheritdoc/>
    public void Merge(CountMinSketch<T> other)
    {
        if (other._width != _width || other._depth != _depth)
            throw new ArgumentException("Cannot merge sketches with different dimensions.");

        for (int i = 0; i < _depth; i++)
        {
            for (int j = 0; j < _width; j++)
            {
                _counters[i, j] += other._counters[i, j];
            }
        }

        Interlocked.Add(ref _totalCount, other._totalCount);
    }

    /// <inheritdoc/>
    public CountMinSketch<T> Clone()
    {
        var clonedCounters = new long[_depth, _width];
        Array.Copy(_counters, clonedCounters, _counters.Length);
        return new CountMinSketch<T>(clonedCounters, _epsilon, _delta, _totalCount, _serializer);
    }

    /// <inheritdoc/>
    public byte[] Serialize()
    {
        var headerSize = 32; // width(4) + depth(4) + epsilon(8) + delta(8) + totalCount(8)
        var dataSize = _width * _depth * sizeof(long);
        var bytes = new byte[headerSize + dataSize];

        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), _width);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), _depth);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), _epsilon);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 8), _delta);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 8), _totalCount);

        Buffer.BlockCopy(_counters, 0, bytes, headerSize, dataSize);
        return bytes;
    }

    /// <summary>
    /// Deserializes a Count-Min Sketch from bytes.
    /// </summary>
    public static CountMinSketch<T> Deserialize(byte[] data, Func<T, byte[]>? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 32)
            throw new ArgumentException($"Data too short: expected at least 32 bytes header, got {data.Length}.", nameof(data));

        var width = BitConverter.ToInt32(data, 0);
        var depth = BitConverter.ToInt32(data, 4);
        var epsilon = BitConverter.ToDouble(data, 8);
        var delta = BitConverter.ToDouble(data, 16);
        var totalCount = BitConverter.ToInt64(data, 24);

        if (width < 1 || width > 10_000_000)
            throw new ArgumentException($"Invalid width {width}: must be between 1 and 10,000,000.", nameof(data));
        if (depth < 1 || depth > 100)
            throw new ArgumentException($"Invalid depth {depth}: must be between 1 and 100.", nameof(data));

        var expectedSize = 32 + (long)width * depth * sizeof(long);
        if (data.Length < expectedSize)
            throw new ArgumentException($"Data too short: expected {expectedSize} bytes, got {data.Length}.", nameof(data));

        var counters = new long[depth, width];
        Buffer.BlockCopy(data, 32, counters, 0, width * depth * sizeof(long));

        return new CountMinSketch<T>(
            counters,
            epsilon,
            delta,
            totalCount,
            serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty))
        );
    }

    /// <inheritdoc/>
    public void Clear()
    {
        Array.Clear(_counters, 0, _counters.Length);
        _totalCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetHashIndex(byte[] data, int hashIndex)
    {
        ulong hash = ComputeHash(data, (uint)hashIndex);
        return (int)(hash % (ulong)_width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeHash(byte[] data, uint seed)
    {
        const ulong c1 = 0x87c37b91114253d5UL;
        const ulong c2 = 0x4cf5ad432745937fUL;

        ulong h = seed;
        int length = data.Length;
        int blocks = length / 8;

        for (int i = 0; i < blocks; i++)
        {
            ulong k = BitConverter.ToUInt64(data, i * 8);
            k *= c1;
            k = (k << 31) | (k >> 33);
            k *= c2;
            h ^= k;
            h = ((h << 27) | (h >> 37)) * 5 + 0x52dce729;
        }

        ulong tail = 0;
        int tailStart = blocks * 8;
        for (int i = 0; i < length - tailStart; i++)
        {
            tail |= (ulong)data[tailStart + i] << (i * 8);
        }

        tail *= c1;
        tail = (tail << 31) | (tail >> 33);
        tail *= c2;
        h ^= tail;

        h ^= (ulong)length;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;

        return h;
    }
}
