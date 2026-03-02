using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Federation-specific bloom filter for per-shard negative lookup rejection.
/// Uses XxHash64 with K different seeds (seed = hashIndex * 0x9E3779B9) for hash functions.
/// 8KB filter with 10 hash functions yields ~0.1% FPR at ~5000 keys, exceeding the 99%+ rejection target.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Shard Bloom Filter (VFED-10)")]
public sealed class ShardBloomFilter
{
    /// <summary>Golden ratio fractional part used as seed multiplier for independent hash functions.</summary>
    private const uint GoldenRatioSeed = 0x9E3779B9;

    private readonly byte[] _bits;

    /// <summary>
    /// Gets the size of the filter in bytes.
    /// </summary>
    public int FilterSizeBytes { get; }

    /// <summary>
    /// Gets the total number of bits in the filter.
    /// </summary>
    public int BitCount { get; }

    /// <summary>
    /// Gets the number of hash functions used.
    /// </summary>
    public int HashFunctionCount { get; }

    /// <summary>
    /// Gets the estimated number of elements added to the filter.
    /// </summary>
    public long EstimatedElementCount { get; private set; }

    /// <summary>
    /// Gets the serialized size of this bloom filter in bytes.
    /// Layout: FilterSizeBytes(4) + HashFunctionCount(4) + EstimatedElementCount(8) + bits.
    /// </summary>
    public int SerializedSize => 16 + FilterSizeBytes;

    /// <summary>
    /// Creates a new shard bloom filter with the specified parameters.
    /// </summary>
    /// <param name="filterSizeBytes">Filter size in bytes (default: 8192 = 8KB).</param>
    /// <param name="hashFunctionCount">Number of hash functions (default: 10 for ~0.1% FPR at ~5000 keys).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when parameters are non-positive.</exception>
    public ShardBloomFilter(int filterSizeBytes = 8192, int hashFunctionCount = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filterSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hashFunctionCount);

        FilterSizeBytes = filterSizeBytes;
        BitCount = filterSizeBytes * 8;
        HashFunctionCount = hashFunctionCount;
        _bits = new byte[filterSizeBytes];
    }

    /// <summary>
    /// Adds a key (as raw UTF8 bytes) to the bloom filter.
    /// </summary>
    /// <param name="keyUtf8">The key bytes to add.</param>
    public void Add(ReadOnlySpan<byte> keyUtf8)
    {
        for (int i = 0; i < HashFunctionCount; i++)
        {
            int bitIndex = GetBitIndex(keyUtf8, i);
            _bits[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
        }

        EstimatedElementCount++;
    }

    /// <summary>
    /// Adds a key (as string) to the bloom filter. Convenience overload that converts to UTF8.
    /// </summary>
    /// <param name="key">The key string to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public void Add(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int maxBytes = Encoding.UTF8.GetMaxByteCount(key.Length);
        Span<byte> buffer = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        int written = Encoding.UTF8.GetBytes(key, buffer);
        Add(buffer[..written]);
    }

    /// <summary>
    /// Returns <c>true</c> if the key might be present (all K bits are set).
    /// Returns <c>false</c> if the key is definitely absent (any bit is unset).
    /// </summary>
    /// <param name="keyUtf8">The key bytes to test.</param>
    /// <returns><c>true</c> if possibly present; <c>false</c> if definitely absent.</returns>
    public bool MayContain(ReadOnlySpan<byte> keyUtf8)
    {
        for (int i = 0; i < HashFunctionCount; i++)
        {
            int bitIndex = GetBitIndex(keyUtf8, i);
            if ((_bits[bitIndex / 8] & (1 << (bitIndex % 8))) == 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns <c>true</c> if the key might be present. Convenience overload that converts to UTF8.
    /// </summary>
    /// <param name="key">The key string to test.</param>
    /// <returns><c>true</c> if possibly present; <c>false</c> if definitely absent.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public bool MayContain(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int maxBytes = Encoding.UTF8.GetMaxByteCount(key.Length);
        Span<byte> buffer = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        int written = Encoding.UTF8.GetBytes(key, buffer);
        return MayContain(buffer[..written]);
    }

    /// <summary>
    /// Serializes this bloom filter to a destination span.
    /// Layout: FilterSizeBytes(int32 LE) + HashFunctionCount(int32 LE) + EstimatedElementCount(int64 LE) + bits.
    /// </summary>
    /// <param name="dest">Destination span of at least <see cref="SerializedSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="dest"/> is too small.</exception>
    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < SerializedSize)
            throw new ArgumentException($"Destination must be at least {SerializedSize} bytes.", nameof(dest));

        BinaryPrimitives.WriteInt32LittleEndian(dest[0..], FilterSizeBytes);
        BinaryPrimitives.WriteInt32LittleEndian(dest[4..], HashFunctionCount);
        BinaryPrimitives.WriteInt64LittleEndian(dest[8..], EstimatedElementCount);
        _bits.AsSpan().CopyTo(dest[16..]);
    }

    /// <summary>
    /// Deserializes a <see cref="ShardBloomFilter"/> from the specified source span.
    /// </summary>
    /// <param name="src">Source span containing serialized bloom filter data.</param>
    /// <returns>The deserialized bloom filter.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="src"/> is too small.</exception>
    public static ShardBloomFilter ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < 16)
            throw new ArgumentException("Source must be at least 16 bytes for header.", nameof(src));

        int filterSizeBytes = BinaryPrimitives.ReadInt32LittleEndian(src[0..]);
        int hashFunctionCount = BinaryPrimitives.ReadInt32LittleEndian(src[4..]);
        long estimatedCount = BinaryPrimitives.ReadInt64LittleEndian(src[8..]);

        if (src.Length < 16 + filterSizeBytes)
            throw new ArgumentException(
                $"Source must be at least {16 + filterSizeBytes} bytes.", nameof(src));

        var filter = new ShardBloomFilter(filterSizeBytes, hashFunctionCount);
        src.Slice(16, filterSizeBytes).CopyTo(filter._bits);
        filter.EstimatedElementCount = estimatedCount;
        return filter;
    }

    /// <summary>
    /// Computes an 8-byte XxHash64 digest of the entire filter bits array.
    /// Useful for embedding in <see cref="CatalogEntry.BloomFilterDigest"/> for quick
    /// change detection (if digest unchanged, no need to reload full filter).
    /// </summary>
    /// <returns>A 64-bit hash digest of the filter contents.</returns>
    public long ComputeDigest()
    {
        return unchecked((long)XxHash64.HashToUInt64(_bits, 0));
    }

    /// <summary>
    /// Resets all bits to zero and resets the element count.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_bits, 0, _bits.Length);
        EstimatedElementCount = 0;
    }

    private int GetBitIndex(ReadOnlySpan<byte> data, int hashIndex)
    {
        long seed = unchecked((long)((uint)hashIndex * GoldenRatioSeed));
        ulong hash = XxHash64.HashToUInt64(data, seed);
        return (int)(hash % (ulong)BitCount);
    }
}
