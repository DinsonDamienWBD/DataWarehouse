using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Production-ready Bloom filter for membership testing with configurable false positive rate.
/// A space-efficient probabilistic data structure that answers "might be in set" or "definitely not in set".
/// Supports serialization, merging, and false positive rate configuration (T85.3, T85.6).
/// </summary>
/// <remarks>
/// Use cases:
/// - Database query optimization (avoid disk reads for non-existent keys)
/// - Network deduplication (avoid sending duplicates)
/// - Cache filtering (quick negative lookups)
/// - Spam detection pre-filtering
///
/// Thread-safety: This implementation is NOT thread-safe. Use locking for concurrent access.
/// </remarks>
public sealed class BloomFilter<T> : IProbabilisticStructure, IMergeable<BloomFilter<T>>
{
    private readonly BitArray _bits;
    private readonly int _hashCount;
    private readonly int _bitCount;
    private readonly Func<T, byte[]> _serializer;
    private long _itemCount; // accessed via Interlocked for 32-bit atomicity (finding P2-559)

    /// <inheritdoc/>
    public string StructureType => "BloomFilter";

    /// <inheritdoc/>
    public long MemoryUsageBytes => (_bitCount + 7) / 8 + 32; // Bits + overhead

    /// <inheritdoc/>
    public double ConfiguredErrorRate { get; }

    /// <inheritdoc/>
    public long ItemCount => Interlocked.Read(ref _itemCount);

    /// <summary>
    /// Gets the number of bits in the filter.
    /// </summary>
    public int BitCount => _bitCount;

    /// <summary>
    /// Gets the number of hash functions used.
    /// </summary>
    public int HashCount => _hashCount;

    /// <summary>
    /// Gets the current estimated false positive rate based on fill ratio.
    /// </summary>
    public double CurrentFalsePositiveRate
    {
        get
        {
            if (_itemCount == 0) return 0;
            // FPR = (1 - e^(-kn/m))^k where k=hash count, n=items, m=bits
            double fillRatio = 1.0 - Math.Exp(-(double)_hashCount * _itemCount / _bitCount);
            return Math.Pow(fillRatio, _hashCount);
        }
    }

    /// <summary>
    /// Creates a new Bloom filter with specified capacity and false positive rate.
    /// </summary>
    /// <param name="expectedItems">Expected number of items to add.</param>
    /// <param name="falsePositiveRate">Desired false positive rate (0.0 to 1.0).</param>
    /// <param name="serializer">Optional custom serializer for type T. Default uses ToString().</param>
    public BloomFilter(long expectedItems = 1_000_000, double falsePositiveRate = 0.01, Func<T, byte[]>? serializer = null)
    {
        if (expectedItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedItems), "Expected items must be positive.");
        if (falsePositiveRate <= 0 || falsePositiveRate >= 1)
            throw new ArgumentOutOfRangeException(nameof(falsePositiveRate), "False positive rate must be between 0 and 1.");

        ConfiguredErrorRate = falsePositiveRate;

        // Calculate optimal size: m = -n*ln(p) / (ln(2)^2)
        _bitCount = Math.Max(64, (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / Math.Pow(Math.Log(2), 2)));

        // Calculate optimal hash count: k = (m/n) * ln(2)
        _hashCount = Math.Max(1, Math.Min(30, (int)Math.Round((double)_bitCount / expectedItems * Math.Log(2))));

        _bits = new BitArray(_bitCount);
        _serializer = serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Creates a Bloom filter from configuration.
    /// </summary>
    public BloomFilter(ProbabilisticConfig config, Func<T, byte[]>? serializer = null)
        : this(config.ExpectedItems, config.FalsePositiveRate, serializer)
    {
    }

    // Private constructor for deserialization/cloning
    private BloomFilter(BitArray bits, int hashCount, double errorRate, long itemCount, Func<T, byte[]> serializer)
    {
        _bits = bits;
        _bitCount = bits.Length;
        _hashCount = hashCount;
        ConfiguredErrorRate = errorRate;
        _itemCount = itemCount;
        _serializer = serializer;
    }

    /// <summary>
    /// Adds an item to the filter.
    /// </summary>
    /// <param name="item">Item to add.</param>
    public void Add(T item)
    {
        var bytes = _serializer(item);
        foreach (var index in GetHashIndices(bytes))
        {
            _bits.Set(index, true);
        }
        Interlocked.Increment(ref _itemCount);
    }

    /// <summary>
    /// Adds multiple items to the filter.
    /// </summary>
    /// <param name="items">Items to add.</param>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    /// <summary>
    /// Tests if an item might be in the set.
    /// </summary>
    /// <param name="item">Item to test.</param>
    /// <returns>True if item might be in set (possible false positive), false if definitely not in set.</returns>
    public bool MightContain(T item)
    {
        var bytes = _serializer(item);
        foreach (var index in GetHashIndices(bytes))
        {
            if (!_bits.Get(index))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Tests membership with probabilistic result metadata.
    /// </summary>
    /// <param name="item">Item to test.</param>
    /// <returns>Probabilistic result with confidence information.</returns>
    public ProbabilisticResult<bool> Contains(T item)
    {
        var result = MightContain(item);
        return new ProbabilisticResult<bool>
        {
            Value = result,
            Confidence = result ? 1.0 - CurrentFalsePositiveRate : 1.0,
            MayBeFalsePositive = result,
            SourceStructure = StructureType
        };
    }

    /// <summary>
    /// Tests if all items might be in the set.
    /// </summary>
    public bool MightContainAll(IEnumerable<T> items)
    {
        return items.All(MightContain);
    }

    /// <summary>
    /// Tests if any item might be in the set.
    /// </summary>
    public bool MightContainAny(IEnumerable<T> items)
    {
        return items.Any(MightContain);
    }

    /// <inheritdoc/>
    public void Merge(BloomFilter<T> other)
    {
        if (other._bitCount != _bitCount)
            throw new ArgumentException("Cannot merge Bloom filters of different sizes.");
        if (other._hashCount != _hashCount)
            throw new ArgumentException("Cannot merge Bloom filters with different hash counts.");

        _bits.Or(other._bits);
        Interlocked.Add(ref _itemCount, Interlocked.Read(ref other._itemCount));
    }

    /// <inheritdoc/>
    public BloomFilter<T> Clone()
    {
        var clonedBits = new BitArray(_bits);
        return new BloomFilter<T>(clonedBits, _hashCount, ConfiguredErrorRate, _itemCount, _serializer);
    }

    /// <inheritdoc/>
    public byte[] Serialize()
    {
        var bytes = new byte[(_bitCount + 7) / 8 + 24]; // bits + header

        // Header: bit count (4), hash count (4), error rate (8), item count (8)
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), _bitCount);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), _hashCount);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), ConfiguredErrorRate);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 8), _itemCount);

        // Bits
        _bits.CopyTo(bytes, 24);
        return bytes;
    }

    /// <summary>
    /// Deserializes a Bloom filter from bytes.
    /// </summary>
    /// <param name="data">Serialized data.</param>
    /// <param name="serializer">Serializer for items.</param>
    /// <returns>Reconstructed Bloom filter.</returns>
    public static BloomFilter<T> Deserialize(byte[] data, Func<T, byte[]>? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 24)
            throw new ArgumentException("Data too short for BloomFilter deserialization header (minimum 24 bytes).", nameof(data));

        var bitCount = BitConverter.ToInt32(data, 0);
        var hashCount = BitConverter.ToInt32(data, 4);
        var errorRate = BitConverter.ToDouble(data, 8);
        var itemCount = BitConverter.ToInt64(data, 16);

        if (bitCount <= 0 || bitCount > 1_000_000_000) // 1 billion bits max (~125 MB)
            throw new ArgumentException($"Invalid bitCount {bitCount} in serialized BloomFilter data.", nameof(data));
        if (hashCount <= 0 || hashCount > 100)
            throw new ArgumentException($"Invalid hashCount {hashCount} in serialized BloomFilter data.", nameof(data));

        int requiredBytes = 24 + (bitCount + 7) / 8;
        if (data.Length < requiredBytes)
            throw new ArgumentException($"Data too short: need {requiredBytes} bytes but got {data.Length}.", nameof(data));

        var bitBytes = new byte[(bitCount + 7) / 8];
        Array.Copy(data, 24, bitBytes, 0, bitBytes.Length);
        var bits = new BitArray(bitBytes);

        return new BloomFilter<T>(
            bits,
            hashCount,
            errorRate,
            itemCount,
            serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty))
        );
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _bits.SetAll(false);
        Interlocked.Exchange(ref _itemCount, 0);
    }

    /// <summary>
    /// Gets the fill ratio (percentage of bits set).
    /// </summary>
    public double FillRatio => (double)CountSetBits() / _bitCount;

    /// <summary>
    /// Estimates the number of items in the filter based on bit saturation.
    /// Useful for merged filters where exact count is unknown.
    /// </summary>
    public long EstimateItemCount()
    {
        int setBits = CountSetBits(); // shared helper avoids duplicate O(n) scan (finding P2-556)

        if (setBits == 0) return 0;
        if (setBits == _bitCount) return long.MaxValue;

        // n = -m * ln(1 - X/m) / k where X = set bits
        return (long)(-_bitCount * Math.Log(1.0 - (double)setBits / _bitCount) / _hashCount);
    }

    // Generates k hash indices using double hashing: h(i) = h1 + i*h2
    private IEnumerable<int> GetHashIndices(byte[] data)
    {
        // Use MurmurHash3-like hashing with two independent hashes
        ulong hash1 = ComputeHash(data, 0);
        ulong hash2 = ComputeHash(data, hash1);

        for (int i = 0; i < _hashCount; i++)
        {
            var combinedHash = hash1 + (ulong)i * hash2;
            yield return (int)(combinedHash % (ulong)_bitCount);
        }
    }

    /// <summary>
    /// Counts the number of set bits (popcount) in the filter in a single O(n) pass.
    /// Shared between <see cref="FillRatio"/> and <see cref="EstimateItemCount"/> (finding P2-556).
    /// </summary>
    private int CountSetBits()
    {
        int setBits = 0;
        for (int i = 0; i < _bits.Length; i++)
        {
            if (_bits[i]) setBits++;
        }
        return setBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeHash(byte[] data, ulong seed)
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
            k = RotateLeft(k, 31);
            k *= c2;
            h ^= k;
            h = RotateLeft(h, 27) * 5 + 0x52dce729;
        }

        // Handle remaining bytes
        ulong tail = 0;
        int tailStart = blocks * 8;
        for (int i = 0; i < length - tailStart; i++)
        {
            tail |= (ulong)data[tailStart + i] << (i * 8);
        }

        tail *= c1;
        tail = RotateLeft(tail, 31);
        tail *= c2;
        h ^= tail;

        // Finalization
        h ^= (ulong)length;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;

        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RotateLeft(ulong value, int count)
    {
        return (value << count) | (value >> (64 - count));
    }
}
