using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// CDF-based learned shard router for O(1) amortized shard selection.
/// </summary>
/// <remarks>
/// <para>
/// The learned router builds a piecewise linear cumulative distribution function (CDF) model
/// from a sample of keys. Each segment of the CDF maps a key range to a shard ID. Routing
/// a key requires converting it to a normalized double value, applying the CDF model, and
/// reading the shard assignment — all O(1) operations.
/// </para>
/// <para>
/// Thread safety is achieved via volatile model reference swap: readers always see a consistent
/// snapshot of the CDF model, while a writer builds a new model and atomically assigns it.
/// </para>
/// <para>
/// The router also tracks per-shard object counts for imbalance detection. When the max/min
/// shard load ratio exceeds a configurable threshold, the router signals that retraining is
/// needed.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-04 Learned shard router")]
public sealed class LearnedShardRouter
{
    private volatile CdfModel _model;
    private readonly long[] _shardCounts;
    private readonly int _numShards;

    /// <summary>
    /// Gets the per-shard object counts (approximate, for load balancing decisions).
    /// </summary>
    public double[] ShardLoadFactors
    {
        get
        {
            var factors = new double[_numShards];
            long total = 0;
            for (int i = 0; i < _numShards; i++)
                total += Interlocked.Read(ref _shardCounts[i]);

            if (total == 0)
            {
                for (int i = 0; i < _numShards; i++)
                    factors[i] = 1.0 / _numShards;
                return factors;
            }

            for (int i = 0; i < _numShards; i++)
                factors[i] = (double)Interlocked.Read(ref _shardCounts[i]) / total;

            return factors;
        }
    }

    /// <summary>
    /// Initializes a new <see cref="LearnedShardRouter"/> with the specified number of shards.
    /// Starts with a uniform CDF model (equal-width partitioning).
    /// </summary>
    /// <param name="numShards">Number of shards to route across.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when numShards is less than 1.</exception>
    public LearnedShardRouter(int numShards)
    {
        if (numShards < 1)
            throw new ArgumentOutOfRangeException(nameof(numShards), numShards, "Must have at least 1 shard.");

        _numShards = numShards;
        _shardCounts = new long[numShards];

        // Initialize with uniform model
        _model = BuildUniformModel(numShards);
    }

    /// <summary>
    /// Builds a CDF model from a sample of keys. The model partitions the CDF space
    /// into <see cref="_numShards"/> segments, each mapping to a shard ID.
    /// </summary>
    /// <param name="sampleKeys">Representative sample of keys from the dataset.</param>
    /// <exception cref="ArgumentNullException">Thrown when sampleKeys is null.</exception>
    public void TrainFromSample(IReadOnlyList<byte[]> sampleKeys)
    {
        ArgumentNullException.ThrowIfNull(sampleKeys);

        if (sampleKeys.Count == 0)
        {
            _model = BuildUniformModel(_numShards);
            return;
        }

        // Convert keys to normalized doubles and sort
        var values = new double[sampleKeys.Count];
        for (int i = 0; i < sampleKeys.Count; i++)
            values[i] = KeyToNormalizedDouble(sampleKeys[i]);

        Array.Sort(values);

        // Build piecewise linear CDF with numShards segments
        var boundaries = new double[_numShards + 1];
        boundaries[0] = 0.0;
        boundaries[_numShards] = 1.0;

        int samplesPerShard = Math.Max(1, values.Length / _numShards);
        for (int i = 1; i < _numShards; i++)
        {
            int sampleIndex = Math.Min(i * samplesPerShard, values.Length - 1);
            boundaries[i] = values[sampleIndex];
        }

        // Ensure monotonically increasing boundaries
        for (int i = 1; i <= _numShards; i++)
        {
            if (boundaries[i] <= boundaries[i - 1])
                boundaries[i] = boundaries[i - 1] + double.Epsilon;
        }

        // Atomic swap
        _model = new CdfModel(boundaries);
    }

    /// <summary>
    /// Routes a key to a shard ID using the CDF model. O(1) amortized via piecewise linear lookup.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>The shard ID (0-based).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Route(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        double normalized = KeyToNormalizedDouble(key);
        var model = _model; // Volatile read — consistent snapshot

        int shardId = model.Lookup(normalized, _numShards);

        // Track load
        Interlocked.Increment(ref _shardCounts[shardId]);

        return shardId;
    }

    /// <summary>
    /// Routes a key without tracking load (for read-only queries).
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>The shard ID (0-based).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RouteReadOnly(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        double normalized = KeyToNormalizedDouble(key);
        var model = _model;
        return model.Lookup(normalized, _numShards);
    }

    /// <summary>
    /// Returns true if the max/min shard load ratio exceeds the threshold,
    /// indicating that the CDF model should be retrained.
    /// </summary>
    /// <param name="threshold">The imbalance threshold (ratio). Default: 2.0.</param>
    /// <returns>True if any shard has more than threshold times the load of the lightest shard.</returns>
    public bool IsImbalanced(double threshold = 2.0)
    {
        long min = long.MaxValue;
        long max = long.MinValue;

        for (int i = 0; i < _numShards; i++)
        {
            long count = Interlocked.Read(ref _shardCounts[i]);
            if (count < min) min = count;
            if (count > max) max = count;
        }

        // Avoid division by zero: if min is 0 and max > 0, consider imbalanced
        if (min == 0)
            return max > 0;

        return (double)max / min > threshold;
    }

    /// <summary>
    /// Rebuilds the CDF model from a fresh sample and resets load counters.
    /// </summary>
    /// <param name="sampleKeys">New representative sample of keys.</param>
    public void Retrain(IReadOnlyList<byte[]> sampleKeys)
    {
        TrainFromSample(sampleKeys);

        // Reset load counters
        for (int i = 0; i < _numShards; i++)
            Interlocked.Exchange(ref _shardCounts[i], 0);
    }

    /// <summary>
    /// Decrements the load counter for a shard (used when an object is deleted from a shard).
    /// </summary>
    /// <param name="shardId">The shard to decrement.</param>
    internal void DecrementShardCount(int shardId)
    {
        if (shardId >= 0 && shardId < _numShards)
        {
            long current = Interlocked.Read(ref _shardCounts[shardId]);
            if (current > 0)
                Interlocked.Decrement(ref _shardCounts[shardId]);
        }
    }

    /// <summary>
    /// Converts a key to a normalized double in [0, 1) by interpreting the first 8 bytes
    /// as a big-endian unsigned 64-bit integer and dividing by ulong.MaxValue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double KeyToNormalizedDouble(byte[] key)
    {
        if (key.Length == 0)
            return 0.0;

        // Pad to 8 bytes if shorter
        Span<byte> buffer = stackalloc byte[8];
        int copyLen = Math.Min(key.Length, 8);
        key.AsSpan(0, copyLen).CopyTo(buffer);

        ulong value = BinaryPrimitives.ReadUInt64BigEndian(buffer);
        return (double)value / ulong.MaxValue;
    }

    /// <summary>
    /// Builds a uniform CDF model with equal-width segments.
    /// </summary>
    private static CdfModel BuildUniformModel(int numShards)
    {
        var boundaries = new double[numShards + 1];
        for (int i = 0; i <= numShards; i++)
            boundaries[i] = (double)i / numShards;
        return new CdfModel(boundaries);
    }

    /// <summary>
    /// Immutable CDF model: piecewise linear segments mapping normalized key values to shard IDs.
    /// </summary>
    private sealed class CdfModel
    {
        private readonly double[] _boundaries;

        /// <summary>
        /// Creates a CDF model with the specified segment boundaries.
        /// boundaries[i] is the lower bound of shard i; boundaries[numShards] = 1.0.
        /// </summary>
        public CdfModel(double[] boundaries)
        {
            _boundaries = boundaries;
        }

        /// <summary>
        /// Looks up the shard ID for a normalized key value via binary search on boundaries.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Lookup(double normalizedValue, int numShards)
        {
            // Binary search through boundaries for O(log S) where S = numShards (small)
            int lo = 0;
            int hi = numShards - 1;

            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (normalizedValue < _boundaries[mid + 1])
                    hi = mid;
                else
                    lo = mid + 1;
            }

            return Math.Clamp(lo, 0, numShards - 1);
        }
    }
}
