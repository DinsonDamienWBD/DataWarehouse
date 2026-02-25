using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// Per-allocation-group bloom filter for O(1) "does this group contain tag=X?" checks.
/// Uses XxHash64 with K different seeds (seed = hashIndex * 0x9E3779B9) for hash functions.
/// 1KB filter with 7 hash functions yields ~1% false positive rate at ~600 elements.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-22 per-allocation-group tag bloom filter")]
public sealed class TagBloomFilter
{
    /// <summary>Golden ratio fractional part used as seed multiplier.</summary>
    private const uint GoldenRatioSeed = 0x9E3779B9;

    private readonly byte[] _bits;

    /// <summary>Size of the filter in bytes.</summary>
    public int FilterSizeBytes { get; }

    /// <summary>Total number of bits in the filter.</summary>
    public int BitCount { get; }

    /// <summary>Number of hash functions used.</summary>
    public int HashFunctionCount { get; }

    /// <summary>
    /// Creates a new bloom filter with the specified parameters.
    /// </summary>
    /// <param name="filterSizeBytes">Filter size in bytes (default: 1024 = 1KB).</param>
    /// <param name="hashFunctionCount">Number of hash functions (default: 7 for ~1% FPR at ~600 elements).</param>
    public TagBloomFilter(int filterSizeBytes = 1024, int hashFunctionCount = 7)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filterSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hashFunctionCount);

        FilterSizeBytes = filterSizeBytes;
        BitCount = filterSizeBytes * 8;
        HashFunctionCount = hashFunctionCount;
        _bits = new byte[filterSizeBytes];
    }

    /// <summary>
    /// Adds a tag (key+value) to the bloom filter.
    /// Hashes (key+value) with K hash functions and sets K bits.
    /// </summary>
    /// <param name="tagKey">Tag key.</param>
    /// <param name="tagValue">Tag value.</param>
    public void Add(string tagKey, string tagValue)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ArgumentNullException.ThrowIfNull(tagValue);

        Span<byte> combined = GetCombinedBytes(tagKey, tagValue);
        for (int i = 0; i < HashFunctionCount; i++)
        {
            int bitIndex = GetBitIndex(combined, i);
            _bits[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
        }
    }

    /// <summary>
    /// Returns true if all K bits are set (tag may be present; false positives possible).
    /// Returns false if any bit is unset (tag definitely not present).
    /// </summary>
    /// <param name="tagKey">Tag key.</param>
    /// <param name="tagValue">Tag value.</param>
    /// <returns>True if possibly present; false if definitely absent.</returns>
    public bool MayContain(string tagKey, string tagValue)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ArgumentNullException.ThrowIfNull(tagValue);

        Span<byte> combined = GetCombinedBytes(tagKey, tagValue);
        for (int i = 0; i < HashFunctionCount; i++)
        {
            int bitIndex = GetBitIndex(combined, i);
            if ((_bits[bitIndex / 8] & (1 << (bitIndex % 8))) == 0)
                return false;
        }

        return true;
    }

    /// <summary>Resets all bits to zero.</summary>
    public void Clear()
    {
        Array.Clear(_bits, 0, _bits.Length);
    }

    /// <summary>Gets the raw bit array for serialization.</summary>
    internal ReadOnlySpan<byte> GetBits() => _bits;

    /// <summary>Sets the raw bit array from deserialized data.</summary>
    internal void SetBits(ReadOnlySpan<byte> data)
    {
        int len = Math.Min(data.Length, _bits.Length);
        data.Slice(0, len).CopyTo(_bits);
    }

    private int GetBitIndex(ReadOnlySpan<byte> data, int hashIndex)
    {
        long seed = unchecked((long)((uint)hashIndex * GoldenRatioSeed));
        ulong hash = XxHash64.HashToUInt64(data, seed);
        return (int)(hash % (ulong)BitCount);
    }

    private static byte[] GetCombinedBytes(string tagKey, string tagValue)
    {
        int keyLen = Encoding.UTF8.GetByteCount(tagKey);
        int valueLen = Encoding.UTF8.GetByteCount(tagValue);
        var buffer = new byte[keyLen + valueLen];
        Encoding.UTF8.GetBytes(tagKey, buffer);
        Encoding.UTF8.GetBytes(tagValue, buffer.AsSpan(keyLen));
        return buffer;
    }
}

/// <summary>
/// Manages one <see cref="TagBloomFilter"/> per allocation group.
/// Enables O(1) group-level tag existence checks to skip entire groups
/// during tag queries when the bloom filter says NO.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-22 per-allocation-group bloom filter set")]
public sealed class TagBloomFilterSet
{
    private readonly TagBloomFilter[] _filters;

    /// <summary>Number of allocation groups managed.</summary>
    public int GroupCount { get; }

    /// <summary>Filter size in bytes per group.</summary>
    public int FilterSizeBytes { get; }

    /// <summary>Number of hash functions per filter.</summary>
    public int HashFunctionCount { get; }

    /// <summary>
    /// Creates a bloom filter set with one filter per allocation group.
    /// </summary>
    /// <param name="groupCount">Number of allocation groups.</param>
    /// <param name="filterSizeBytes">Filter size in bytes per group (default: 1024).</param>
    /// <param name="hashFunctionCount">Hash function count per filter (default: 7).</param>
    public TagBloomFilterSet(int groupCount, int filterSizeBytes = 1024, int hashFunctionCount = 7)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupCount);

        GroupCount = groupCount;
        FilterSizeBytes = filterSizeBytes;
        HashFunctionCount = hashFunctionCount;
        _filters = new TagBloomFilter[groupCount];
        for (int i = 0; i < groupCount; i++)
            _filters[i] = new TagBloomFilter(filterSizeBytes, hashFunctionCount);
    }

    /// <summary>
    /// Adds a tag to the bloom filter for the specified allocation group.
    /// </summary>
    /// <param name="groupId">Allocation group ID (0-based).</param>
    /// <param name="tagKey">Tag key.</param>
    /// <param name="tagValue">Tag value.</param>
    public void AddToGroup(int groupId, string tagKey, string tagValue)
    {
        ValidateGroupId(groupId);
        _filters[groupId].Add(tagKey, tagValue);
    }

    /// <summary>
    /// Returns true if the group's bloom filter indicates the tag might be present.
    /// False means the tag is definitely NOT in this group.
    /// </summary>
    /// <param name="groupId">Allocation group ID (0-based).</param>
    /// <param name="tagKey">Tag key.</param>
    /// <param name="tagValue">Tag value.</param>
    /// <returns>True if possibly present; false if definitely absent.</returns>
    public bool GroupMayContainTag(int groupId, string tagKey, string tagValue)
    {
        ValidateGroupId(groupId);
        return _filters[groupId].MayContain(tagKey, tagValue);
    }

    /// <summary>
    /// Returns only group IDs where the tag might exist (skips groups where
    /// bloom filter says definitely NO). Enables group-level skip during tag queries.
    /// </summary>
    /// <param name="tagKey">Tag key.</param>
    /// <param name="tagValue">Tag value.</param>
    /// <returns>Group IDs where the tag might be present.</returns>
    public IReadOnlyList<int> FilterGroups(string tagKey, string tagValue)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ArgumentNullException.ThrowIfNull(tagValue);

        var result = new List<int>();
        for (int i = 0; i < GroupCount; i++)
        {
            if (_filters[i].MayContain(tagKey, tagValue))
                result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Serializes all filters into a contiguous byte array.
    /// Layout: groupCount * filterSizeBytes bytes (filters concatenated in order).
    /// </summary>
    public byte[] Serialize()
    {
        var data = new byte[GroupCount * FilterSizeBytes];
        for (int i = 0; i < GroupCount; i++)
        {
            _filters[i].GetBits().CopyTo(data.AsSpan(i * FilterSizeBytes));
        }
        return data;
    }

    /// <summary>
    /// Deserializes a bloom filter set from a contiguous byte array.
    /// </summary>
    /// <param name="data">Serialized data (groupCount * filterSizeBytes bytes).</param>
    /// <param name="groupCount">Number of allocation groups.</param>
    /// <param name="filterSizeBytes">Filter size in bytes per group.</param>
    /// <param name="hashFunctionCount">Hash function count per filter (default: 7).</param>
    /// <returns>The deserialized bloom filter set.</returns>
    public static TagBloomFilterSet Deserialize(
        ReadOnlySpan<byte> data,
        int groupCount,
        int filterSizeBytes = 1024,
        int hashFunctionCount = 7)
    {
        var set = new TagBloomFilterSet(groupCount, filterSizeBytes, hashFunctionCount);
        for (int i = 0; i < groupCount; i++)
        {
            int offset = i * filterSizeBytes;
            if (offset + filterSizeBytes <= data.Length)
                set._filters[i].SetBits(data.Slice(offset, filterSizeBytes));
        }
        return set;
    }

    private void ValidateGroupId(int groupId)
    {
        if (groupId < 0 || groupId >= GroupCount)
            throw new ArgumentOutOfRangeException(nameof(groupId),
                $"Group ID must be in [0, {GroupCount}). Got: {groupId}");
    }
}
